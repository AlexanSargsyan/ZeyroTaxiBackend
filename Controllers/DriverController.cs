using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taxi_API.Data;
using Taxi_API.DTOs;
using Taxi_API.Models;
using Taxi_API.Services;

namespace Taxi_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DriverController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IStorageService _storage;
        private readonly IEmailService _email;
        private readonly IImageComparisonService _imageComparison;
        private readonly IPaymentService _paymentService;
        private readonly ILogger<DriverController> _logger;

        public DriverController(AppDbContext db, IStorageService storage, IEmailService email, IImageComparisonService imageComparison, IPaymentService paymentService, ILogger<DriverController> logger)
        {
            _db = db;
            _storage = storage;
            _email = email;
            _imageComparison = imageComparison;
            _paymentService = paymentService;
            _logger = logger;
        }

        [Authorize]
        [HttpPost("submit")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> SubmitDriverProfile([FromForm] DriverProfileSubmissionRequest request)
        {
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var user = await _db.Users.Include(u => u.DriverProfile).ThenInclude(dp => dp.Photos).FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return NotFound();

            // Collect all uploaded files
            var uploadedFiles = new Dictionary<string, IFormFile>
            {
                ["passport_front"] = request.PassportFront,
                ["passport_back"] = request.PassportBack,
                ["dl_front"] = request.DlFront,
                ["dl_back"] = request.DlBack,
                ["car_front"] = request.CarFront,
                ["car_back"] = request.CarBack,
                ["car_left"] = request.CarLeft,
                ["car_right"] = request.CarRight,
                ["car_interior"] = request.CarInterior,
                ["tech_passport"] = request.TechPassport
            };

            // Validate all required files are present
            var required = new[] { "passport_front", "passport_back", "dl_front", "dl_back", "car_front", "car_back", "car_left", "car_right", "car_interior", "tech_passport" };
            var missing = required.Where(r => uploadedFiles[r] == null).ToList();
            
            if (missing.Any())
            {
                return BadRequest($"Missing required photos: {string.Join(", ", missing)}");
            }

            // Validate file sizes (max 10MB each, 50MB total)
            long totalSize = uploadedFiles.Values.Where(f => f != null).Sum(f => f.Length);
            if (totalSize > 50 * 1024 * 1024)
            {
                return BadRequest("Total files size exceeds 50MB");
            }

            foreach (var file in uploadedFiles.Values.Where(f => f != null))
            {
                if (file.Length > 10 * 1024 * 1024)
                {
                    return BadRequest($"File '{file.FileName}' exceeds 10MB limit");
                }
            }

            // Save all files
            var saved = new List<Photo>();

            foreach (var kvp in uploadedFiles.Where(f => f.Value != null))
            {
                var type = kvp.Key;
                var file = kvp.Value;

                var fileName = $"{userId}_{type}_{DateTime.UtcNow.Ticks}{Path.GetExtension(file.FileName)}";
                
                using (var stream = file.OpenReadStream())
                {
                    var path = await _storage.SaveFileAsync(stream, fileName);
                    saved.Add(new Photo 
                    { 
                        UserId = user.Id, 
                        Path = path, 
                        Type = type, 
                        Size = file.Length 
                    });
                }
            }

            // Create or update driver profile
            if (user.DriverProfile == null)
            {
                user.DriverProfile = new DriverProfile 
                { 
                    UserId = user.Id, 
                    Photos = saved, 
                    SubmittedAt = DateTime.UtcNow 
                };
                _db.DriverProfiles.Add(user.DriverProfile);
            }
            else
            {
                // Remove old photos of the same types
                var oldPhotos = user.DriverProfile.Photos
                    .Where(p => required.Contains(p.Type))
                    .ToList();
                
                foreach (var old in oldPhotos)
                {
                    user.DriverProfile.Photos.Remove(old);
                }

                user.DriverProfile.Photos.AddRange(saved);
                user.DriverProfile.SubmittedAt = DateTime.UtcNow;
            }

            // Compare passport front face and DL front face
            var passport = saved.FirstOrDefault(p => p.Type == "passport_front");
            var dl = saved.FirstOrDefault(p => p.Type == "dl_front");
            var comparisonOk = false;
            
            if (passport != null && dl != null)
            {
                try
                {
                    var (score, match) = await _imageComparison.CompareFacesAsync(passport.Path, dl.Path);
                    comparisonOk = match;
                    
                    if (!match)
                    {
                        await _email.SendAsync(user.Phone + "@example.com", "Face mismatch", 
                            $"Passport and driving license photos do not match (score {score:F2}).");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Face comparison failed");
                }
            }

            // Check car exterior images for damage
            var exteriorKeys = new[] { "car_front", "car_back", "car_left", "car_right" };
            var exteriorPaths = saved.Where(p => exteriorKeys.Contains(p.Type)).Select(p => p.Path).ToList();
            var carOk = false;
            
            try
            {
                var (damageScore, isDamaged) = await _imageComparison.CheckCarDamageAsync(exteriorPaths);
                carOk = !isDamaged;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Car damage check failed");
            }

            // Persist automated check results
            if (user.DriverProfile != null)
            {
                user.DriverProfile.FaceMatch = comparisonOk;
                user.DriverProfile.CarOk = carOk;
            }

            // OCR extraction
            try
            {
                var ocr = HttpContext.RequestServices.GetService(typeof(IOcrService)) as IOcrService;
                if (ocr != null && user.DriverProfile != null)
                {
                    await ExtractDocumentInformation(ocr, saved, user.DriverProfile, user);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OCR extraction failed");
                try 
                { 
                    await _email.SendAsync(user.Phone + "@example.com", "OCR error", ex.Message); 
                } 
                catch { }
            }

            user.IsDriver = false; // Remain false until verification
            await _db.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                faceMatch = comparisonOk,
                carOk = carOk,
                photosUploaded = saved.Count,
                passportNumber = user.DriverProfile?.PassportNumber,
                licenseNumber = user.DriverProfile?.LicenseNumber,
                carMake = user.DriverProfile?.CarMake,
                carModel = user.DriverProfile?.CarModel,
                carYear = user.DriverProfile?.CarYear,
                message = "Driver profile submitted successfully. Awaiting verification."
            });
        }

        private async Task ExtractDocumentInformation(IOcrService ocr, List<Photo> photos, DriverProfile profile, User user)
        {
            // Extract passport info
            var passportFront = photos.FirstOrDefault(p => p.Type == "passport_front");
            if (passportFront != null)
            {
                var passportText = await ocr.ExtractTextAsync(passportFront.Path, "eng");
                if (!string.IsNullOrWhiteSpace(passportText))
                {
                    var pn = System.Text.RegularExpressions.Regex.Match(passportText, @"[A-Z]{1,2}[0-9]{5,8}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (pn.Success) profile.PassportNumber = pn.Value.ToUpper();

                    var dtm = System.Text.RegularExpressions.Regex.Match(passportText, @"(20\d{2}|19\d{2})[-/.](0[1-9]|1[0-2])[-/.](0[1-9]|[12][0-9]|3[01])");
                    if (!dtm.Success)
                    {
                        dtm = System.Text.RegularExpressions.Regex.Match(passportText, @"(0[1-9]|[12][0-9]|3[01])[-/.](0[1-9]|1[0-2])[-/.](20\d{2}|19\d{2})");
                    }
                    if (dtm.Success && DateTime.TryParse(dtm.Value, out var exp)) profile.PassportExpiry = exp;

                    var lines = passportText.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                    var nameLine = lines.FirstOrDefault(l => l.Split(' ').All(tok => tok.All(ch => char.IsLetter(ch) || ch == '-' || ch == ' ')) && l.Length > 4 && l.Length < 50);
                    if (!string.IsNullOrWhiteSpace(nameLine)) profile.PassportName = nameLine;

                    var countryMatch = System.Text.RegularExpressions.Regex.Match(passportText, @"(United States|USA|Armenia|Russia|France|Germany|United Kingdom|UK)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (countryMatch.Success) profile.PassportCountry = countryMatch.Value;
                }
            }

            // Extract license info
            var dlFront = photos.FirstOrDefault(p => p.Type == "dl_front");
            if (dlFront != null)
            {
                var dlText = await ocr.ExtractTextAsync(dlFront.Path, "eng");
                if (!string.IsNullOrWhiteSpace(dlText))
                {
                    var ln = System.Text.RegularExpressions.Regex.Match(dlText, @"[A-Z0-9\-]{5,20}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (ln.Success) profile.LicenseNumber = ln.Value.ToUpper();

                    var dtm = System.Text.RegularExpressions.Regex.Match(dlText, @"(20\d{2}|19\d{2})[-/.](0[1-9]|1[0-2])[-/.](0[1-9]|[12][0-9]|3[01])");
                    if (!dtm.Success)
                    {
                        dtm = System.Text.RegularExpressions.Regex.Match(dlText, @"(0[1-9]|[12][0-9]|3[01])[-/.](0[1-9]|1[0-2])[-/.](20\d{2}|19\d{2})");
                    }
                    if (dtm.Success && DateTime.TryParse(dtm.Value, out var exp)) profile.LicenseExpiry = exp;

                    var lines = dlText.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                    var nameLine = lines.FirstOrDefault(l => l.Split(' ').All(tok => tok.All(ch => char.IsLetter(ch) || ch == '-' || ch == ' ')) && l.Length > 4 && l.Length < 50);
                    if (!string.IsNullOrWhiteSpace(nameLine)) profile.LicenseName = nameLine;

                    var countryMatch = System.Text.RegularExpressions.Regex.Match(dlText, @"(United States|USA|Armenia|Russia|France|Germany|United Kingdom|UK)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (countryMatch.Success) profile.LicenseIssuingCountry = countryMatch.Value;
                }
            }

            // Extract tech passport info
            var techPassport = photos.FirstOrDefault(p => p.Type == "tech_passport");
            if (techPassport != null)
            {
                var techText = await ocr.ExtractTextAsync(techPassport.Path, "eng");
                if (!string.IsNullOrWhiteSpace(techText))
                {
                    var yearMatch = System.Text.RegularExpressions.Regex.Match(techText, @"\b(19|20)\d{2}\b");
                    if (yearMatch.Success && int.TryParse(yearMatch.Value, out var year))
                    {
                        profile.CarYear = year;
                    }

                    var makeModel = ExtractMakeModelFromText(techText);
                    if (!string.IsNullOrWhiteSpace(makeModel.make)) profile.CarMake = makeModel.make;
                    if (!string.IsNullOrWhiteSpace(makeModel.model)) profile.CarModel = makeModel.model;

                    var colorMatch = System.Text.RegularExpressions.Regex.Match(techText, @"\b(white|black|silver|gray|grey|red|blue|green|yellow|orange|brown|beige)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (colorMatch.Success) profile.CarColor = colorMatch.Value;

                    var plateMatch = System.Text.RegularExpressions.Regex.Match(techText, @"\b[A-Z0-9]{2,3}[-\s]?[0-9]{2,4}[-\s]?[A-Z0-9]{0,3}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (plateMatch.Success) profile.CarPlate = plateMatch.Value.ToUpper();

                    if (profile.CarYear.HasValue && profile.CarYear.Value < 2010)
                    {
                        await _email.SendAsync(user.Phone + "@example.com", "Car too old", 
                            $"Detected car year {profile.CarYear.Value} is below allowed threshold (2010).");
                    }
                }
            }
        }

        [Authorize]
        [HttpGet("status")]
        public async Task<IActionResult> Status()
        {
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var user = await _db.Users.Include(u => u.DriverProfile).FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return NotFound();
            return Ok(new DriverStatusResponse(user.DriverProfile?.Approved ?? false));
        }

        [Authorize]
        [HttpGet("car")]
        public async Task<IActionResult> GetCarInfo()
        {
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var profile = await _db.DriverProfiles.Include(dp => dp.Photos).FirstOrDefaultAsync(dp => dp.UserId == userId);
            if (profile == null) return NotFound();

            return Ok(new
            {
                make = profile.CarMake,
                model = profile.CarModel,
                color = profile.CarColor,
                plate = profile.CarPlate,
                year = profile.CarYear,
                approved = profile.Approved,
                carOk = profile.CarOk
            });
        }

        [Authorize]
        [HttpGet("profile")]
        public async Task<IActionResult> GetProfile()
        {
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var user = await _db.Users
                .Include(u => u.DriverProfile)
                    .ThenInclude(dp => dp.Photos)
                .Include(u => u.Orders)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null) return NotFound();

            var driverProfile = user.DriverProfile;

            var profileDto = new
            {
                id = user.Id,
                phone = user.Phone,
                name = user.Name,
                isDriver = user.IsDriver,
                phoneVerified = user.PhoneVerified,
                createdAt = user.CreatedAt,
                driverProfile = driverProfile == null ? null : new
                {
                    id = driverProfile.Id,
                    approved = driverProfile.Approved,
                    submittedAt = driverProfile.SubmittedAt,
                    faceMatch = driverProfile.FaceMatch,
                    carOk = driverProfile.CarOk,
                    carMake = driverProfile.CarMake,
                    carModel = driverProfile.CarModel,
                    carColor = driverProfile.CarColor,
                    carPlate = driverProfile.CarPlate,
                    carYear = driverProfile.CarYear,
                    photos = driverProfile.Photos.Select(p => new { id = p.Id, path = p.Path, type = p.Type, uploadedAt = p.UploadedAt }).ToArray()
                },
                recentOrders = user.Orders.OrderByDescending(o => o.CreatedAt).Take(20).Select(o => new
                {
                    id = o.Id,
                    action = o.Action,
                    status = o.Status,
                    pickup = o.Pickup,
                    destination = o.Destination,
                    price = o.Price,
                    createdAt = o.CreatedAt
                }).ToArray()
            };

            return Ok(profileDto);
        }

        [Authorize]
        [HttpPatch("location")]
        public async Task<IActionResult> UpdateLocation([FromBody] DriverLocationUpdateRequest req)
        {
            if (req == null) return BadRequest("Body required");

            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var profile = await _db.DriverProfiles.FirstOrDefaultAsync(dp => dp.UserId == userId);
            if (profile == null) return NotFound("Driver profile not found");

            profile.CurrentLat = req.Lat;
            profile.CurrentLng = req.Lng;
            profile.LastLocationAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            // notify sockets if socket service available
            try
            {
                var ws = HttpContext.RequestServices.GetService(typeof(ISocketService)) as ISocketService;
                if (ws != null)
                {
                    // broadcast to any listeners (use userId as key)
                    await ws.NotifyOrderEventAsync(Guid.Empty, "driverLocation", new { driverId = userId, lat = req.Lat, lng = req.Lng });
                }
            }
            catch { }

            return Ok(new { ok = true });
        }

        [Authorize]
        [HttpPost("stripe/onboard")]
        public async Task<IActionResult> CreateStripeOnboard()
        {
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var user = await _db.Users.Include(u => u.DriverProfile).FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return NotFound();

            var email = (user.Name ?? user.Phone) + "@example.com";
            var accountId = await _paymentService.CreateConnectedAccountAsync(email, "US");
            if (string.IsNullOrEmpty(accountId)) return StatusCode(502, "Failed to create connected account");

            if (user.DriverProfile == null)
            {
                user.DriverProfile = new DriverProfile { UserId = user.Id };
                _db.DriverProfiles.Add(user.DriverProfile);
            }
            user.DriverProfile.StripeAccountId = accountId;
            await _db.SaveChangesAsync();

            var refresh = "https://yourapp.example.com/stripe-refresh";
            var ret = "https://yourapp.example.com/stripe-return";
            var link = await _paymentService.CreateAccountLinkAsync(accountId, refresh, ret);

            return Ok(new { accountId, link });
        }

        [Authorize]
        [HttpGet("identity")]
        public async Task<IActionResult> GetIdentity()
        {
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var user = await _db.Users.Include(u => u.DriverProfile).ThenInclude(dp => dp.Photos).FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return NotFound();

            var dp = user.DriverProfile;
            if (dp == null) return NotFound();

            return Ok(new
            {
                passport = new
                {
                    passportNumber = dp.PassportNumber,
                    passportName = dp.PassportName,
                    passportExpiry = dp.PassportExpiry,
                    passportCountry = dp.PassportCountry
                },
                license = new
                {
                    licenseNumber = dp.LicenseNumber,
                    licenseName = dp.LicenseName,
                    licenseExpiry = dp.LicenseExpiry,
                    licenseIssuingCountry = dp.LicenseIssuingCountry
                },
                car = new
                {
                    carMake = dp.CarMake,
                    carModel = dp.CarModel,
                    carYear = dp.CarYear,
                    carColor = dp.CarColor,
                    carPlate = dp.CarPlate
                },
                photos = dp.Photos.Select(p => new { id = p.Id, path = p.Path, type = p.Type, uploadedAt = p.UploadedAt })
            });
        }

        public class TwoSideUploadRequest
        {
            public IFormFile Front { get; set; } = null!;
            public IFormFile Back { get; set; } = null!;
        }

        [Authorize]
        [HttpPost("identity/passport")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadPassport(
            [FromForm] TwoSideUploadRequest request)
        {
            var front = request.Front;
            var back = request.Back;

            if (front == null || back == null)
                return BadRequest("Both 'front' and 'back' passport images are required");

            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var user = await _db.Users.Include(u => u.DriverProfile).ThenInclude(dp => dp.Photos).FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return NotFound();

            if (user.DriverProfile == null)
            {
                user.DriverProfile = new DriverProfile { UserId = user.Id };
                _db.DriverProfiles.Add(user.DriverProfile);
            }

            // Validate file sizes (max 10MB each)
            if (front.Length > 10 * 1024 * 1024 || back.Length > 10 * 1024 * 1024)
            {
                return BadRequest("Each file must be less than 10MB");
            }

            // Save images
            var saved = new List<Photo>();
            var frontFileName = $"{userId}_passport_front_{DateTime.UtcNow.Ticks}.jpg";
            var backFileName = $"{userId}_passport_back_{DateTime.UtcNow.Ticks}.jpg";

            using (var frontStream = front.OpenReadStream())
            {
                var frontPath = await _storage.SaveFileAsync(frontStream, frontFileName);
                saved.Add(new Photo { UserId = userId, Path = frontPath, Type = "passport_front", Size = front.Length });
            }

            using (var backStream = back.OpenReadStream())
            {
                var backPath = await _storage.SaveFileAsync(backStream, backFileName);
                saved.Add(new Photo { UserId = userId, Path = backPath, Type = "passport_back", Size = back.Length });
            }

            // Remove old passport photos
            var oldPhotos = user.DriverProfile.Photos.Where(p => p.Type == "passport_front" || p.Type == "passport_back").ToList();
            foreach (var old in oldPhotos)
            {
                user.DriverProfile.Photos.Remove(old);
            }

            // Add new photos
            user.DriverProfile.Photos.AddRange(saved);

            // Extract passport information using OCR
            var ocr = HttpContext.RequestServices.GetService(typeof(IOcrService)) as IOcrService;
            if (ocr != null)
            {
                try
                {
                    var frontPath = saved.First(p => p.Type == "passport_front").Path;
                    var passportText = await ocr.ExtractTextAsync(frontPath, "eng");

                    if (!string.IsNullOrWhiteSpace(passportText))
                    {
                        // Extract passport number (pattern: 2 letters followed by 5-8 digits)
                        var numberMatch = System.Text.RegularExpressions.Regex.Match(passportText, @"[A-Z]{1,2}[0-9]{5,8}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (numberMatch.Success)
                        {
                            user.DriverProfile.PassportNumber = numberMatch.Value.ToUpper();
                        }

                        // Extract expiry date
                        var dateMatch = System.Text.RegularExpressions.Regex.Match(passportText, @"(20\d{2}|19\d{2})[-/.](0[1-9]|1[0-2])[-/.](0[1-9]|[12][0-9]|3[01])");
                        if (!dateMatch.Success)
                        {
                            dateMatch = System.Text.RegularExpressions.Regex.Match(passportText, @"(0[1-9]|[12][0-9]|3[01])[-/.](0[1-9]|1[0-2])[-/.](20\d{2}|19\d{2})");
                        }
                        if (dateMatch.Success && DateTime.TryParse(dateMatch.Value, out var expiry))
                        {
                            user.DriverProfile.PassportExpiry = expiry;
                        }

                        // Extract name (line with only letters, spaces, and hyphens)
                        var lines = passportText.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                        var nameLine = lines.FirstOrDefault(l => l.Split(' ').All(tok => tok.All(ch => char.IsLetter(ch) || ch == '-' || ch == ' ')) && l.Length > 4 && l.Length < 50);
                        if (!string.IsNullOrWhiteSpace(nameLine))
                        {
                            user.DriverProfile.PassportName = nameLine;
                        }

                        // Extract country (look for common country keywords)
                        var countryMatch = System.Text.RegularExpressions.Regex.Match(passportText, @"(United States|USA|Armenia|Russia|France|Germany|United Kingdom|UK)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (countryMatch.Success)
                        {
                            user.DriverProfile.PassportCountry = countryMatch.Value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log error but don't fail the request
                    await _email.SendAsync(user.Phone + "@example.com", "Passport OCR error", ex.Message);
                }
            }

            await _db.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                passportNumber = user.DriverProfile.PassportNumber,
                passportName = user.DriverProfile.PassportName,
                passportExpiry = user.DriverProfile.PassportExpiry,
                passportCountry = user.DriverProfile.PassportCountry,
                photos = saved.Select(p => new { path = p.Path, type = p.Type })
            });
        }

        [Authorize]
        [HttpPost("identity/license")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadLicense(
            [FromForm] TwoSideUploadRequest request)
        {
            var front = request.Front;
            var back = request.Back;

            if (front == null || back == null)
                return BadRequest("Both 'front' and 'back' license images are required");


            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var user = await _db.Users.Include(u => u.DriverProfile).ThenInclude(dp => dp.Photos).FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return NotFound();

            if (user.DriverProfile == null)
            {
                user.DriverProfile = new DriverProfile { UserId = user.Id };
                _db.DriverProfiles.Add(user.DriverProfile);
            }

            // Validate file sizes
            if (front.Length > 10 * 1024 * 1024 || back.Length > 10 * 1024 * 1024)
            {
                return BadRequest("Each file must be less than 10MB");
            }

            // Save images
            var saved = new List<Photo>();
            var frontFileName = $"{userId}_dl_front_{DateTime.UtcNow.Ticks}.jpg";
            var backFileName = $"{userId}_dl_back_{DateTime.UtcNow.Ticks}.jpg";

            using (var frontStream = front.OpenReadStream())
            {
                var frontPath = await _storage.SaveFileAsync(frontStream, frontFileName);
                saved.Add(new Photo { UserId = userId, Path = frontPath, Type = "dl_front", Size = front.Length });
            }

            using (var backStream = back.OpenReadStream())
            {
                var backPath = await _storage.SaveFileAsync(backStream, backFileName);
                saved.Add(new Photo { UserId = userId, Path = backPath, Type = "dl_back", Size = back.Length });
            }

            // Remove old license photos
            var oldPhotos = user.DriverProfile.Photos.Where(p => p.Type == "dl_front" || p.Type == "dl_back").ToList();
            foreach (var old in oldPhotos)
            {
                user.DriverProfile.Photos.Remove(old);
            }

            // Add new photos
            user.DriverProfile.Photos.AddRange(saved);

            // Extract license information using OCR
            var ocr = HttpContext.RequestServices.GetService(typeof(IOcrService)) as IOcrService;
            if (ocr != null)
            {
                try
                {
                    var frontPath = saved.First(p => p.Type == "dl_front").Path;
                    var licenseText = await ocr.ExtractTextAsync(frontPath, "eng");

                    if (!string.IsNullOrWhiteSpace(licenseText))
                    {
                        // Extract license number (alphanumeric, 5-20 characters)
                        var numberMatch = System.Text.RegularExpressions.Regex.Match(licenseText, @"[A-Z0-9\-]{5,20}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (numberMatch.Success)
                        {
                            user.DriverProfile.LicenseNumber = numberMatch.Value.ToUpper();
                        }

                        // Extract expiry date
                        var dateMatch = System.Text.RegularExpressions.Regex.Match(licenseText, @"(20\d{2}|19\d{2})[-/.](0[1-9]|1[0-2])[-/.](0[1-9]|[12][0-9]|3[01])");
                        if (!dateMatch.Success)
                        {
                            dateMatch = System.Text.RegularExpressions.Regex.Match(licenseText, @"(0[1-9]|[12][0-9]|3[01])[-/.](0[1-9]|1[0-2])[-/.](20\d{2}|19\d{2})");
                        }
                        if (dateMatch.Success && DateTime.TryParse(dateMatch.Value, out var expiry))
                        {
                            user.DriverProfile.LicenseExpiry = expiry;
                        }

                        // Extract name
                        var lines = licenseText.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                        var nameLine = lines.FirstOrDefault(l => l.Split(' ').All(tok => tok.All(ch => char.IsLetter(ch) || ch == '-' || ch == ' ')) && l.Length > 4 && l.Length < 50);
                        if (!string.IsNullOrWhiteSpace(nameLine)) user.DriverProfile.LicenseName = nameLine;

                        // Extract issuing country
                        var countryMatch = System.Text.RegularExpressions.Regex.Match(licenseText, @"(United States|USA|Armenia|Russia|France|Germany|United Kingdom|UK)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (countryMatch.Success) user.DriverProfile.LicenseIssuingCountry = countryMatch.Value;
                    }
                }
                catch (Exception ex)
                {
                    await _email.SendAsync(user.Phone + "@example.com", "License OCR error", ex.Message);
                }
            }

            await _db.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                licenseNumber = user.DriverProfile.LicenseNumber,
                licenseName = user.DriverProfile.LicenseName,
                licenseExpiry = user.DriverProfile.LicenseExpiry,
                licenseIssuingCountry = user.DriverProfile.LicenseIssuingCountry,
                photos = saved.Select(p => new { path = p.Path, type = p.Type })
            });
        }

        [Authorize]
        [HttpPost("identity/car-registration")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadCarRegistration(
            [FromForm] TwoSideUploadRequest request)
        {
            var front = request.Front;
            var back = request.Back;

            if (front == null || back == null)
                return BadRequest("Both 'front' and 'back' car registration images are required");

            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var user = await _db.Users.Include(u => u.DriverProfile).ThenInclude(dp => dp.Photos).FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return NotFound();

            if (user.DriverProfile == null)
            {
                user.DriverProfile = new DriverProfile { UserId = user.Id };
                _db.DriverProfiles.Add(user.DriverProfile);
            }

            // Validate file sizes
            if (front.Length > 10 * 1024 * 1024 || back.Length > 10 * 1024 * 1024)
            {
                return BadRequest("Each file must be less than 10MB");
            }

            // Save images
            var saved = new List<Photo>();
            var frontFileName = $"{userId}_tech_passport_front_{DateTime.UtcNow.Ticks}.jpg";
            var backFileName = $"{userId}_tech_passport_back_{DateTime.UtcNow.Ticks}.jpg";

            using (var frontStream = front.OpenReadStream())
            {
                var frontPath = await _storage.SaveFileAsync(frontStream, frontFileName);
                saved.Add(new Photo { UserId = userId, Path = frontPath, Type = "tech_passport_front", Size = front.Length });
            }

            using (var backStream = back.OpenReadStream())
            {
                var backPath = await _storage.SaveFileAsync(backStream, backFileName);
                saved.Add(new Photo { UserId = userId, Path = backPath, Type = "tech_passport_back", Size = back.Length });
            }

            // Remove old tech passport photos
            var oldPhotos = user.DriverProfile.Photos.Where(p => p.Type == "tech_passport_front" || p.Type == "tech_passport_back" || p.Type == "tech_passport").ToList();
            foreach (var old in oldPhotos)
            {
                user.DriverProfile.Photos.Remove(old);
            }

            // Add new photos
            user.DriverProfile.Photos.AddRange(saved);

            // Extract car registration information using OCR
            var ocr = HttpContext.RequestServices.GetService(typeof(IOcrService)) as IOcrService;
            if (ocr != null)
            {
                try
                {
                    var frontPath = saved.First(p => p.Type == "tech_passport_front").Path;
                    var regText = await ocr.ExtractTextAsync(frontPath, "eng");

                    if (!string.IsNullOrWhiteSpace(regText))
                    {
                        // Extract year
                        var yearMatch = System.Text.RegularExpressions.Regex.Match(regText, @"\b(19|20)\d{2}\b");
                        if (yearMatch.Success && int.TryParse(yearMatch.Value, out var year))
                        {
                            user.DriverProfile.CarYear = year;
                        }

                        // Extract make and model
                        var makeModel = ExtractMakeModelFromText(regText);
                        if (!string.IsNullOrWhiteSpace(makeModel.make))
                        {
                            user.DriverProfile.CarMake = makeModel.make;
                        }
                        if (!string.IsNullOrWhiteSpace(makeModel.model))
                        {
                            user.DriverProfile.CarModel = makeModel.model;
                        }

                        // Extract color (look for common color names)
                        var colorMatch = System.Text.RegularExpressions.Regex.Match(regText, @"\b(white|black|silver|gray|grey|red|blue|green|yellow|orange|brown|beige)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (colorMatch.Success)
                        {
                            user.DriverProfile.CarColor = colorMatch.Value;
                        }

                        // Extract plate number (various formats: ABC123, AB-123-CD, etc.)
                        var plateMatch = System.Text.RegularExpressions.Regex.Match(regText, @"\b[A-Z0-9]{2,3}[-\s]?[0-9]{2,4}[-\s]?[A-Z0-9]{0,3}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (plateMatch.Success)
                        {
                            user.DriverProfile.CarPlate = plateMatch.Value.ToUpper();
                        }

                        // Check if car is too old
                        if (user.DriverProfile.CarYear.HasValue && user.DriverProfile.CarYear.Value < 2010)
                        {
                            await _email.SendAsync(user.Phone + "@example.com", "Car too old", $"Detected car year {user.DriverProfile.CarYear.Value} is below allowed threshold (2010).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    await _email.SendAsync(user.Phone + "@example.com", "Car registration OCR error", ex.Message);
                }
            }

            await _db.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                carMake = user.DriverProfile.CarMake,
                carModel = user.DriverProfile.CarModel,
                carYear = user.DriverProfile.CarYear,
                carColor = user.DriverProfile.CarColor,
                carPlate = user.DriverProfile.CarPlate,
                photos = saved.Select(p => new { path = p.Path, type = p.Type })
            });
        }

        private (string make, string model) ExtractMakeModelFromText(string text)
        {
            // Simple heuristic: look for lines containing keywords
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            string make = string.Empty, model = string.Empty;
            foreach (var l in lines)
            {
                var lower = l.ToLowerInvariant();
                if (lower.Contains("make") || lower.Contains("brand") || lower.Contains("?????"))
                {
                    var parts = l.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        make = parts[1].Trim();
                    }
                }
                if (lower.Contains("model") || lower.Contains("??????"))
                {
                    var parts = l.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        model = parts[1].Trim();
                    }
                }
                // fallback: if a line contains two words and one looks like manufacturer
                if (string.IsNullOrEmpty(make) && l.Split(' ').Length == 2)
                {
                    // e.g., Toyota Corolla
                    var p = l.Split(' ');
                    make = p[0]; model = p[1];
                }
                if (!string.IsNullOrEmpty(make) && !string.IsNullOrEmpty(model)) break;
            }
            return (make, model);
        }
    }
}

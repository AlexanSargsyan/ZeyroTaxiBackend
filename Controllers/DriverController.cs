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

        public DriverController(AppDbContext db, IStorageService storage, IEmailService email, IImageComparisonService imageComparison)
        {
            _db = db;
            _storage = storage;
            _email = email;
            _imageComparison = imageComparison;
        }

        [Authorize]
        [HttpPost("submit")]
        public async Task<IActionResult> SubmitDriverProfile()
        {
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var user = await _db.Users.Include(u => u.DriverProfile).FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return NotFound();

            // Expect photos as base64 strings in form fields (e.g., passport_front, passport_back, ...)
            var required = new[] { "passport_front", "passport_back", "dl_front", "dl_back", "car_front", "car_back", "car_left", "car_right", "car_interior", "tech_passport" };

            var form = Request.Form;

            if (!required.All(r => form.ContainsKey(r) && !string.IsNullOrWhiteSpace(form[r])))
            {
                return BadRequest("Missing required photos");
            }

            // Decode and save provided base64 strings
            var saved = new List<Photo>();
            long totalSize = 0;

            foreach (var key in required)
            {
                var value = form[key].ToString();
                if (string.IsNullOrWhiteSpace(value)) continue; // already validated, but safe

                // strip data URI prefix if present
                var base64 = value;
                var commaIdx = base64.IndexOf(',');
                if (commaIdx >= 0)
                {
                    base64 = base64.Substring(commaIdx + 1);
                }

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(base64);
                }
                catch
                {
                    return BadRequest($"Invalid base64 for {key}");
                }

                totalSize += bytes.Length;
                if (totalSize > 30 * 1024 * 1024) return BadRequest("Total files size exceeds 30MB");

                using var ms = new MemoryStream(bytes);
                var fileName = $"{userId}_{key}_{DateTime.UtcNow.Ticks}.jpg";
                var path = await _storage.SaveFileAsync(ms, fileName);
                saved.Add(new Photo { UserId = user.Id, Path = path, Type = key, Size = bytes.Length });
            }

            if (user.DriverProfile == null)
            {
                user.DriverProfile = new DriverProfile { UserId = user.Id, Photos = saved, SubmittedAt = DateTime.UtcNow };
                _db.DriverProfiles.Add(user.DriverProfile);
            }
            else
            {
                user.DriverProfile.Photos.AddRange(saved);
                user.DriverProfile.SubmittedAt = DateTime.UtcNow;
            }

            // compare passport front face and DL front face
            var passport = saved.FirstOrDefault(p => p.Type == "passport_front");
            var dl = saved.FirstOrDefault(p => p.Type == "dl_front");
            var comparisonOk = false;
            if (passport != null && dl != null)
            {
                var (score, match) = await _imageComparison.CompareFacesAsync(passport.Path, dl.Path);
                comparisonOk = match;
                if (!match)
                {
                    // notify about mismatch
                    await _email.SendAsync(user.Phone + "@example.com", "Face mismatch", $"Passport and driving license photos do not match (score {score:F2}).");
                }
            }

            // check car exterior images for damage
            var exteriorKeys = new[] { "car_front", "car_back", "car_left", "car_right" };
            var exteriorPaths = saved.Where(p => exteriorKeys.Contains(p.Type)).Select(p => p.Path).ToList();
            var (damageScore, carOk) = await _imageComparison.CheckCarDamageAsync(exteriorPaths);

            // persist automated check results
            if (user.DriverProfile != null)
            {
                user.DriverProfile.FaceMatch = comparisonOk;
                user.DriverProfile.CarOk = carOk;
            }

            user.IsDriver = false; // remain false until verification
            await _db.SaveChangesAsync();

            // simple check on tech passport year - filename contains year or metadata not available; we assume client sends year in form
            if (Request.Form.TryGetValue("tech_year", out var techYearStr) && int.TryParse(techYearStr, out var techYear))
            {
                if (techYear < 2010)
                {
                    await _email.SendAsync(user.Phone + "@example.com", "Car too old", "The car year is below allowed threshold.");
                }
            }

            // return whether automated checks passed
            return Ok(new DriverStatusResponse(comparisonOk && carOk));
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
    }
}
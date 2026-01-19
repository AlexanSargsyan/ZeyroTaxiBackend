using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Taxi_API.Data;
using Taxi_API.DTOs;
using Taxi_API.Models;
using Taxi_API.Services;
using Microsoft.Extensions.Configuration;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Taxi_API.Controllers
{
    [ApiController]
    [Route("api/driver")]
    public class DriverAuthController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ITokenService _tokenService;
        private readonly IEmailService _email;
        private readonly ISmsService _sms;
        private readonly IConfiguration _config;

        public DriverAuthController(AppDbContext db, ITokenService tokenService, IEmailService email, ISmsService sms, IConfiguration config)
        {
            _db = db;
            _tokenService = tokenService;
            _email = email;
            _sms = sms;
            _config = config;
        }

        [HttpPost("request-code")]
        public async Task<IActionResult> RequestCode([FromBody] RequestCodeRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Phone)) return BadRequest("Phone is required");

            var norm = PhoneNumberValidator.Normalize(req.Phone);
            if (norm == null) return BadRequest("Invalid phone format");
            var phone = norm;

            var code = new Random().Next(100000, 999999).ToString("D6");

            var session = new AuthSession
            {
                Id = Guid.NewGuid(),
                Phone = phone,
                Code = code,
                Verified = false,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            };

            _db.AuthSessions.Add(session);
            await _db.SaveChangesAsync();

            // Send via SMS (Veloconnect)
            try
            {
                await _sms.SendSmsAsync(phone, $"Your driver verification code is: {code}");
            }
            catch (Exception ex)
            {
                var logger = HttpContext.RequestServices.GetService(typeof(ILogger<DriverAuthController>)) as ILogger<DriverAuthController>;
                logger?.LogError(ex, "Failed to send SMS to {Phone}", phone);
                // Continue anyway
            }

            // Return only sent status and code (NOT authSessionId)
            return Ok(new { sent = true, code = code });
        }

        [HttpPost("resend")]
        public async Task<IActionResult> Resend([FromBody] ResendRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Phone)) return BadRequest("Phone is required");

            var phoneNorm = PhoneNumberValidator.Normalize(req.Phone);
            if (phoneNorm == null) return BadRequest("Invalid phone format");
            var phone = phoneNorm;

            var session = await _db.AuthSessions
                .Where(s => s.Phone == phone && !s.Verified && s.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefaultAsync();

            if (session == null) return BadRequest("No active session found");

            session.Code = RandomNumberGenerator.GetInt32(100000, 999999).ToString("D6");
            session.ExpiresAt = DateTime.UtcNow.AddMinutes(10);
            await _db.SaveChangesAsync();

            // Send via SMS (Veloconnect)
            try
            {
                await _sms.SendSmsAsync(phone, $"Your driver verification code is: {session.Code}");
            }
            catch (Exception ex)
            {
                var logger = HttpContext.RequestServices.GetService(typeof(ILogger<DriverAuthController>)) as ILogger<DriverAuthController>;
                logger?.LogError(ex, "Failed to send SMS to {Phone}", phone);
                // Continue anyway
            }

            // Return sent status and the new code
            return Ok(new { sent = true, code = session.Code });
        }


        [HttpPost("verify")]
        public async Task<IActionResult> Verify([FromBody] VerifyRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Phone) || string.IsNullOrWhiteSpace(req.Code)) return BadRequest("Phone and Code are required");

            var phoneNorm = PhoneNumberValidator.Normalize(req.Phone);
            if (phoneNorm == null) return BadRequest("Invalid phone format");
            var phone = phoneNorm;

            var session = await _db.AuthSessions
                .Where(s => s.Phone == phone)
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefaultAsync();

            if (session == null) return BadRequest("No session found for this phone");
            if (session.ExpiresAt <= DateTime.UtcNow) return BadRequest("Code expired");
            if (session.Code != req.Code) return BadRequest("Invalid code");

            session.Verified = true;
            await _db.SaveChangesAsync();

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Phone == phone);
            if (user == null)
            {
                user = new User { Id = Guid.NewGuid(), Phone = phone, Name = req.Name, IsDriver = true, PhoneVerified = true };
                _db.Users.Add(user);
                await _db.SaveChangesAsync();
            }
            else
            {
                user.IsDriver = true;
                user.PhoneVerified = true;
                if (!string.IsNullOrWhiteSpace(req.Name)) user.Name = req.Name;
                await _db.SaveChangesAsync();
            }

            return Ok(new { AuthSessionId = session.Id.ToString() });
        }

        // Accept token in body: { "token": "..." } — Swagger will show request body
        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login([FromBody] AuthTokenRequest? body)
        {
            string? tokenToValidate = null;

            // prefer token in body if provided
            if (body != null && !string.IsNullOrWhiteSpace(body.Token))
            {
                tokenToValidate = body.Token.Trim();
            }
            else
            {
                // fallback to Authorization header
                var authHeader = Request.Headers["Authorization"].ToString();
                if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer "))
                {
                    tokenToValidate = authHeader.Substring("Bearer ".Length).Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(tokenToValidate)) return Unauthorized(new { error = "No token provided" });

            try
            {
                var key = _config["Jwt:Key"] ?? "very_secret_key_please_change";
                var issuer = _config["Jwt:Issuer"] ?? "TaxiApi";

                // derive 256-bit key same as JwtTokenService
                var keyBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = false,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };

                var handler = new JwtSecurityTokenHandler();
                var principal = handler.ValidateToken(tokenToValidate, validationParameters, out var validatedToken);

                // extract subject claim (user id)
                var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                          ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(sub, out var userId)) return Unauthorized(new { error = "Invalid subject in token" });

                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsDriver);
                if (user == null) return Unauthorized(new { error = "No driver user for token subject" });

                // Get the most recent verified session for this user
                var latestSession = await _db.AuthSessions
                    .Where(s => s.Phone == user.Phone && s.Verified)
                    .OrderByDescending(s => s.CreatedAt)
                    .FirstOrDefaultAsync();

                // Issue a fresh token
                var newToken = _tokenService.GenerateToken(user);
                return Ok(new AuthResponse(newToken, latestSession?.Id.ToString() ?? string.Empty));
            }
            catch (Microsoft.IdentityModel.Tokens.SecurityTokenExpiredException)
            {
                return Unauthorized(new { error = "Token expired" });
            }
            catch (Microsoft.IdentityModel.Tokens.SecurityTokenInvalidSignatureException)
            {
                return Unauthorized(new { error = "Invalid token signature" });
            }
            catch (Microsoft.IdentityModel.Tokens.SecurityTokenException ex)
            {
                return Unauthorized(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                // log exception
                try { var logger = HttpContext.RequestServices.GetService(typeof(ILogger<DriverAuthController>)) as ILogger; logger?.LogError(ex, "Unexpected error validating token"); } catch { }
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout([FromBody] AuthTokenRequest? body)
        {
            // allow token via body or Authorization header
            string? tokenToValidate = null;
            if (body != null && !string.IsNullOrWhiteSpace(body.Token)) tokenToValidate = body.Token.Trim();
            else
            {
                var authHeader = Request.Headers["Authorization"].ToString();
                if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer "))
                {
                    tokenToValidate = authHeader.Substring("Bearer ".Length).Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(tokenToValidate)) return Unauthorized(new { error = "No token provided" });

            try
            {
                var key = _config["Jwt:Key"] ?? "very_secret_key_please_change";
                var issuer = _config["Jwt:Issuer"] ?? "TaxiApi";
                var keyBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = false,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };

                var handler = new JwtSecurityTokenHandler();
                var principal = handler.ValidateToken(tokenToValidate, validationParameters, out var validatedToken);

                var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                          ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(sub, out var userId)) return Unauthorized(new { error = "Invalid subject in token" });

                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsDriver);
                if (user == null) return Unauthorized(new { error = "No driver user for token subject" });

                var now = DateTime.UtcNow;
                var sessions = await _db.AuthSessions.Where(s => s.Phone == user.Phone && s.ExpiresAt > now).ToListAsync();
                foreach (var s in sessions)
                {
                    s.ExpiresAt = now;
                    s.Verified = false;
                }

                await _db.SaveChangesAsync();

                return Ok(new { loggedOut = true });
            }
            catch (Microsoft.IdentityModel.Tokens.SecurityTokenExpiredException)
            {
                return Unauthorized(new { error = "Token expired" });
            }
            catch (Microsoft.IdentityModel.Tokens.SecurityTokenException ex)
            {
                return Unauthorized(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                var logger = HttpContext.RequestServices.GetService(typeof(ILogger<DriverAuthController>)) as ILogger<DriverAuthController>;
                logger?.LogError(ex, "Unexpected error in logout");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        [HttpPost("auth")]
        public async Task<IActionResult> Auth([FromBody] AuthRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.AuthSessionId) || string.IsNullOrWhiteSpace(req.Code))
                return BadRequest("AuthSessionId and Code are required");

            if (!Guid.TryParse(req.AuthSessionId, out var sessionId)) return BadRequest("Invalid AuthSessionId");

            var session = await _db.AuthSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null) return BadRequest("Invalid session");

            if (session.ExpiresAt < DateTime.UtcNow) return BadRequest("Code expired");

            if (session.Code != req.Code) return BadRequest("Invalid code");

            // require verification step completed first
            if (!session.Verified) return BadRequest("Session not verified");

            var phone = session.Phone;
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Phone == phone && u.IsDriver);
            if (user == null) return BadRequest("Driver not registered or verified");

            var token = _tokenService.GenerateToken(user);
            return Ok(new AuthResponse(token, session.Id.ToString()));
        }
    }
}

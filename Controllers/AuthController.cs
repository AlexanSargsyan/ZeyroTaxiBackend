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
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ITokenService _tokenService;
        private readonly IEmailService _email;
        private readonly ISmsService _sms;
        private readonly IConfiguration _config;
        private readonly ILogger<AuthController> _logger;

        public AuthController(AppDbContext db, ITokenService tokenService, IEmailService email, ISmsService sms, IConfiguration config, ILogger<AuthController> logger)
        {
            _db = db;
            _tokenService = tokenService;
            _email = email;
            _sms = sms;
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Request SMS verification code for user authentication
        /// </summary>
        [HttpPost("request-code")]
        public async Task<IActionResult> RequestCode([FromBody] RequestCodeRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Phone)) return BadRequest("Phone is required");

            var norm = PhoneNumberValidator.Normalize(req.Phone);
            if (norm == null) return BadRequest("Invalid phone format");
            var phone = norm;

            // Check if a user with this phone number already exists with a different name
            if (!string.IsNullOrWhiteSpace(req.Name))
            {
                var existingUser = await _db.Users.FirstOrDefaultAsync(u => u.Phone == phone);
                if (existingUser != null && !string.Equals(existingUser.Name, req.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new
                    {
                        error = "Phone number already registered",
                        message = $"This phone number is already registered with a different name. Please use the correct name: {existingUser.Name}",
                        existingName = existingUser.Name
                    });
                }
            }

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

            // Send code via SMS (Veloconnect)
            try
            {
                await _sms.SendSmsAsync(phone, $"Your verification code is: {code}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send SMS to {Phone}", phone);
                // Continue anyway and return the code
            }

            // Return only sent status and code (NOT authSessionId)
            return Ok(new { Sent = true, Code = code });
        }

        /// <summary>
        /// Resend SMS verification code
        /// </summary>
        [HttpPost("resend")]
        public async Task<IActionResult> Resend([FromBody] ResendRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Phone))
                return BadRequest("Phone is required");

            var phoneNorm = PhoneNumberValidator.Normalize(req.Phone);
            if (phoneNorm == null) return BadRequest("Invalid phone format");
            var phone = phoneNorm;

            var session = await _db.AuthSessions
                .Where(s => s.Phone == phone && !s.Verified)
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefaultAsync();

            if (session == null)
                return BadRequest("No active session found");

            session.Code = RandomNumberGenerator.GetInt32(100000, 999999).ToString("D6");
            session.ExpiresAt = DateTime.UtcNow.AddMinutes(10);

            await _db.SaveChangesAsync();

            // Send code via SMS (Veloconnect)
            try
            {
                await _sms.SendSmsAsync(phone, $"Your verification code is: {session.Code}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send SMS to {Phone}", phone);
                // Continue anyway
            }

            // Return sent status and the new code
            return Ok(new { Sent = true, Code = session.Code });
        }

        /// <summary>
        /// Verify SMS code and create user if needed
        /// </summary>
        [HttpPost("verify")]
        public async Task<IActionResult> Verify([FromBody] VerifyRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Phone) || string.IsNullOrWhiteSpace(req.Code)) return BadRequest("Phone and Code are required");

            var phoneNorm = PhoneNumberValidator.Normalize(req.Phone);
            if (phoneNorm == null) return BadRequest("Invalid phone format");
            var phone = phoneNorm;

            var session = await _db.AuthSessions.OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(s => s.Phone == phone && s.Code == req.Code && s.ExpiresAt > DateTime.UtcNow);
            if (session == null) return BadRequest("Invalid or expired code");

            session.Verified = true;
            await _db.SaveChangesAsync();

            // create or fetch user
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Phone == phone);
            if (user == null)
            {
                user = new User { Id = Guid.NewGuid(), Phone = phone, Name = req.Name };
                _db.Users.Add(user);
                await _db.SaveChangesAsync();
            }

            // Generate token immediately
            var token = _tokenService.GenerateToken(user);

            // Return both AuthSessionId and token
            return Ok(new { AuthSessionId = session.Id.ToString(), Token = token });
        }

        /// <summary>
        /// Get JWT token using verified session
        /// </summary>
        [HttpPost("auth")] // combined login/register using session id + code
        public async Task<IActionResult> Auth([FromBody] AuthRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.AuthSessionId) || string.IsNullOrWhiteSpace(req.Code))
                return BadRequest("AuthSessionId and Code are required");

            if (!Guid.TryParse(req.AuthSessionId, out var sessionId)) return BadRequest("Invalid AuthSessionId");

            var session = await _db.AuthSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null) return BadRequest("Invalid session");

            if (session.ExpiresAt < DateTime.UtcNow) return BadRequest("Code expired");

            if (session.Code != req.Code) return BadRequest("Invalid code");

            // require that session is verified
            if (!session.Verified) return BadRequest("Session not verified");

            var phone = session.Phone;
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Phone == phone);
            if (user == null)
            {
                user = new User { Id = Guid.NewGuid(), Phone = phone, Name = req.Name };
                _db.Users.Add(user);
                await _db.SaveChangesAsync();
            }

            var token = _tokenService.GenerateToken(user);
            return Ok(new { token });
        }

        /// <summary>
        /// Logout user and expire all sessions
        /// </summary>
        [HttpPost("logout")]
        public async Task<IActionResult> Logout([FromBody] AuthTokenRequest? body)
        {
            // allow token via body or Authorization header
            string? tokenToValidate = null;
            if (body != null && !string.IsNullOrWhiteSpace(body.Token)) 
                tokenToValidate = body.Token.Trim();
            else
            {
                var authHeader = Request.Headers["Authorization"].ToString();
                if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer "))
                {
                    tokenToValidate = authHeader.Substring("Bearer ".Length).Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(tokenToValidate)) 
                return Unauthorized(new { error = "No token provided" });

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
                if (!Guid.TryParse(sub, out var userId)) 
                    return Unauthorized(new { error = "Invalid subject in token" });

                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null) 
                    return Unauthorized(new { error = "User not found" });

                // Expire all active sessions for this user
                var now = DateTime.UtcNow;
                var sessions = await _db.AuthSessions
                    .Where(s => s.Phone == user.Phone && s.ExpiresAt > now)
                    .ToListAsync();
                
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
                // Could add logging here
                return StatusCode(500, new { error = "Internal server error" });
            }
        }
    }
}
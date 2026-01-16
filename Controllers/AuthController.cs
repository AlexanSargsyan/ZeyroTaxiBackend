using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Taxi_API.Data;
using Taxi_API.DTOs;
using Taxi_API.Models;
using Taxi_API.Services;
using Microsoft.Extensions.Configuration;

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

        public AuthController(AppDbContext db, ITokenService tokenService, IEmailService email, ISmsService sms, IConfiguration config)
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

            // send code via email/sms. For simplicity use email service with phone@example.com
            await _email.SendAsync(phone + "@example.com", "Your login code", $"Your code is: {code}");

            // Check if we should return the code in response (for development/testing only)
            var returnCodeInResponse = _config.GetValue<bool>("Auth:ReturnCodeInResponse", false);

            if (returnCodeInResponse)
            {
                return Ok(new { Sent = true, Code = code, AuthSessionId = session.Id.ToString() });
            }

            // Production: Don't return code for security
            return Ok(new { Sent = true });
        }

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

            await _email.SendAsync(
                phone + "@example.com",
                "Your login code (resend)",
                $"Your code is: {session.Code}"
            );

            return Ok(new { Sent = true });
        }

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

            // Now return AuthSessionId so client can use it if needed
            return Ok(new { AuthSessionId = session.Id.ToString() });
        }

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
            return Ok(new AuthResponse(token, session.Id.ToString()));
        }
    }
}
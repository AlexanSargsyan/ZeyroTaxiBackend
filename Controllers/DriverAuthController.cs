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
    [Route("api/driver")]
    public class DriverAuthController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ITokenService _tokenService;
        private readonly IEmailService _email;

        public DriverAuthController(AppDbContext db, ITokenService tokenService, IEmailService email)
        {
            _db = db;
            _tokenService = tokenService;
            _email = email;
        }

        [HttpPost("request-code")]
        public async Task<IActionResult> RequestCode([FromBody] RequestCodeRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Phone)) return BadRequest("Phone is required");

            var code = new Random().Next(100000, 999999).ToString();
            var session = new AuthSession
            {
                Id = Guid.NewGuid(),
                Phone = req.Phone,
                Code = code,
                Verified = false,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            };

            _db.AuthSessions.Add(session);
            await _db.SaveChangesAsync();

            await _email.SendAsync(req.Phone + "@example.com", "Your driver login code", $"Your code is: {code}");

            // Optionally store name temporarily by creating/updating user record with IsDriver = false until verification
            if (!string.IsNullOrWhiteSpace(req.Name))
            {
                var existing = await _db.Users.FirstOrDefaultAsync(u => u.Phone == req.Phone);
                if (existing == null)
                {
                    var user = new User { Id = Guid.NewGuid(), Phone = req.Phone, Name = req.Name, IsDriver = false };
                    _db.Users.Add(user);
                    await _db.SaveChangesAsync();
                }
                else
                {
                    existing.Name = req.Name;
                    await _db.SaveChangesAsync();
                }
            }

            return Ok(new { AuthSessionId = session.Id.ToString(), ExpiresAt = session.ExpiresAt });
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

            // mark verified
            session.Verified = true;
            await _db.SaveChangesAsync();

            var phone = session.Phone;
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Phone == phone);

            if (user != null && user.IsDriver)
            {
                // existing driver -> login
                var token = _tokenService.GenerateToken(user);
                return Ok(new AuthResponse(token, session.Id.ToString()));
            }

            // not an existing driver -> register as driver but do NOT auto-login (client will call login afterwards)
            if (user == null)
            {
                user = new User { Id = Guid.NewGuid(), Phone = phone, Name = req.Name, IsDriver = true };
                _db.Users.Add(user);
                await _db.SaveChangesAsync();
                return Ok(new DriverAuthResponse(null, session.Id.ToString(), true));
            }

            // user exists but not driver
            user.IsDriver = true;
            if (!string.IsNullOrWhiteSpace(req.Name)) user.Name = req.Name;
            await _db.SaveChangesAsync();

            // return registered marker without token (driver should login explicitly)
            return Ok(new DriverAuthResponse(null, session.Id.ToString(), true));
        }

        [Authorize]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] object? body)
        {
            // Token-based login: clients should use /api/auth/auth flow. For drivers, after registration they can request code and authenticate to get token.
            // This endpoint simply returns the token for the authenticated driver (token already provided) — not typical. Keep for compatibility.
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsDriver);
            if (user == null) return Unauthorized();
            var token = _tokenService.GenerateToken(user);
            return Ok(new AuthResponse(token, string.Empty));
        }

        [Authorize]
        [HttpPost("logout")]
        public IActionResult Logout()
        {
            // JWTs are stateless. For logout, client should delete token. Optionally implement token revocation.
            return Ok(new { LoggedOut = true });
        }
    }
}

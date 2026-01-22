using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Taxi_API.Data;
using Taxi_API.DTOs;
using Taxi_API.Models;

namespace Taxi_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ScheduleController : ControllerBase
    {
        private readonly AppDbContext _db;

        public ScheduleController(AppDbContext db)
        {
            _db = db;
        }

        // Helper method to extract user ID from claims
        private Guid? GetUserIdFromClaims()
        {
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                         ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                         ?? User.FindFirst("sub")?.Value
                         ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
            
            if (string.IsNullOrEmpty(userIdStr))
            {
                return null;
            }
            
            if (Guid.TryParse(userIdStr, out var userId))
            {
                return userId;
            }
            
            return null;
        }

        /// <summary>
        /// Create a new scheduled ride plan
        /// </summary>
        [Authorize]
        [HttpPost]
        public async Task<IActionResult> CreateSchedule([FromBody] CreateScheduleRequest req)
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            if (req == null || req.Entries == null || req.Entries.Length == 0) return BadRequest("Entries required");

            var plan = new ScheduledPlan
            {
                UserId = userId.Value,
                Name = req.Name,
                EntriesJson = JsonSerializer.Serialize(req.Entries),
                CreatedAt = DateTime.UtcNow
            };
            _db.ScheduledPlans.Add(plan);
            await _db.SaveChangesAsync();

            var entries = JsonSerializer.Deserialize<ScheduleEntry[]>(plan.EntriesJson) ?? Array.Empty<ScheduleEntry>();
            return Ok(new ScheduledPlanResponse(plan.Id, plan.Name, entries));
        }

        /// <summary>
        /// Get all user scheduled ride plans
        /// </summary>
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> GetSchedules()
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            var plans = await _db.ScheduledPlans.Where(s => s.UserId == userId.Value).ToListAsync();
            var res = plans.Select(p => {
                var entries = JsonSerializer.Deserialize<ScheduleEntry[]>(p.EntriesJson) ?? Array.Empty<ScheduleEntry>();
                return new ScheduledPlanResponse(p.Id, p.Name, entries);
            }).ToArray();

            return Ok(res);
        }
    }
}

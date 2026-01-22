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
        /// <remarks>
        /// Create recurring taxi orders by scheduling the same route for multiple days.
        /// Each entry can specify different days of the week when the order should be created.
        /// 
        /// **Example Request:**
        /// ```json
        /// {
        ///   "name": "Work Week Commute",
        ///   "entries": [
        ///     {
        ///       "name": "Morning to Office",
        ///       "pickupAddress": "Home, 123 Main St",
        ///       "pickupLat": 40.1872,
        ///       "pickupLng": 44.5152,
        ///       "destinationAddress": "Office, 456 Business Ave",
        ///       "destinationLat": 40.1776,
        ///       "destinationLng": 44.5126,
        ///       "days": [1, 2, 3, 4, 5],
        ///       "time": "08:30",
        ///       "tariff": "economy",
        ///       "paymentMethod": "card",
        ///       "pet": false,
        ///       "child": false
        ///     },
        ///     {
        ///       "name": "Evening Return Home",
        ///       "pickupAddress": "Office, 456 Business Ave",
        ///       "pickupLat": 40.1776,
        ///       "pickupLng": 44.5126,
        ///       "destinationAddress": "Home, 123 Main St",
        ///       "destinationLat": 40.1872,
        ///       "destinationLng": 44.5152,
        ///       "days": [1, 2, 3, 4, 5],
        ///       "time": "18:00",
        ///       "tariff": "economy",
        ///       "paymentMethod": "card"
        ///     }
        ///   ]
        /// }
        /// ```
        /// 
        /// **Days of Week:**
        /// - 0 = Sunday
        /// - 1 = Monday
        /// - 2 = Tuesday
        /// - 3 = Wednesday
        /// - 4 = Thursday
        /// - 5 = Friday
        /// - 6 = Saturday
        /// 
        /// **Time Format:** 24-hour format (HH:mm), e.g., "08:30", "17:00"
        /// 
        /// **Tariff Options:** electro, economy, comfort, business, premium
        /// </remarks>
        /// <response code="200">Schedule plan created successfully</response>
        /// <response code="400">Invalid request data</response>
        /// <response code="401">Unauthorized - missing or invalid token</response>
        [Authorize]
        [HttpPost("plans")]
        public async Task<IActionResult> CreateSchedule([FromBody] CreateScheduleRequest req)
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            if (req == null || req.Entries == null || req.Entries.Length == 0) 
                return BadRequest("At least one schedule entry is required");

            // Validate entries
            foreach (var entry in req.Entries)
            {
                if (entry.Days == null || entry.Days.Length == 0)
                    return BadRequest("Each entry must have at least one day specified");

                if (string.IsNullOrWhiteSpace(entry.Time))
                    return BadRequest("Time is required for each entry");

                // Validate time format (HH:mm)
                if (!TimeSpan.TryParse(entry.Time, out _))
                    return BadRequest($"Invalid time format: {entry.Time}. Use 24-hour format (HH:mm)");

                // Validate coordinates
                if (entry.PickupLat == 0 || entry.PickupLng == 0 || entry.DestinationLat == 0 || entry.DestinationLng == 0)
                    return BadRequest("Valid pickup and destination coordinates are required");
            }

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
            return Ok(new ScheduledPlanResponse(plan.Id, plan.Name, entries, plan.CreatedAt));
        }

        /// <summary>
        /// Get all user scheduled ride plans
        /// </summary>
        /// <response code="200">Returns list of all scheduled plans</response>
        /// <response code="401">Unauthorized - missing or invalid token</response>
        [Authorize]
        [HttpGet("plans")]
        public async Task<IActionResult> GetSchedules()
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            var plans = await _db.ScheduledPlans
                .Where(s => s.UserId == userId.Value)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync();

            var res = plans.Select(p => {
                var entries = JsonSerializer.Deserialize<ScheduleEntry[]>(p.EntriesJson) ?? Array.Empty<ScheduleEntry>();
                return new ScheduledPlanResponse(p.Id, p.Name, entries, p.CreatedAt);
            }).ToArray();

            return Ok(res);
        }

        /// <summary>
        /// Get a specific scheduled plan by ID
        /// </summary>
        /// <response code="200">Returns the scheduled plan</response>
        /// <response code="404">Plan not found</response>
        /// <response code="401">Unauthorized - missing or invalid token</response>
        [Authorize]
        [HttpGet("plans/{id}")]
        public async Task<IActionResult> GetSchedule(Guid id)
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            var plan = await _db.ScheduledPlans
                .Where(s => s.Id == id && s.UserId == userId.Value)
                .FirstOrDefaultAsync();

            if (plan == null) return NotFound("Schedule plan not found");

            var entries = JsonSerializer.Deserialize<ScheduleEntry[]>(plan.EntriesJson) ?? Array.Empty<ScheduleEntry>();
            return Ok(new ScheduledPlanResponse(plan.Id, plan.Name, entries, plan.CreatedAt));
        }

        /// <summary>
        /// Update a scheduled plan
        /// </summary>
        /// <response code="200">Plan updated successfully</response>
        /// <response code="404">Plan not found</response>
        /// <response code="400">Invalid request data</response>
        /// <response code="401">Unauthorized - missing or invalid token</response>
        [Authorize]
        [HttpPut("plans/{id}")]
        public async Task<IActionResult> UpdateSchedule(Guid id, [FromBody] CreateScheduleRequest req)
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            var plan = await _db.ScheduledPlans
                .Where(s => s.Id == id && s.UserId == userId.Value)
                .FirstOrDefaultAsync();

            if (plan == null) return NotFound("Schedule plan not found");

            if (req == null || req.Entries == null || req.Entries.Length == 0) 
                return BadRequest("At least one schedule entry is required");

            // Validate entries
            foreach (var entry in req.Entries)
            {
                if (entry.Days == null || entry.Days.Length == 0)
                    return BadRequest("Each entry must have at least one day specified");

                if (string.IsNullOrWhiteSpace(entry.Time))
                    return BadRequest("Time is required for each entry");

                if (!TimeSpan.TryParse(entry.Time, out _))
                    return BadRequest($"Invalid time format: {entry.Time}. Use 24-hour format (HH:mm)");
            }

            plan.Name = req.Name;
            plan.EntriesJson = JsonSerializer.Serialize(req.Entries);
            await _db.SaveChangesAsync();

            var entries = JsonSerializer.Deserialize<ScheduleEntry[]>(plan.EntriesJson) ?? Array.Empty<ScheduleEntry>();
            return Ok(new ScheduledPlanResponse(plan.Id, plan.Name, entries, plan.CreatedAt));
        }

        /// <summary>
        /// Delete a scheduled plan
        /// </summary>
        /// <response code="200">Plan deleted successfully</response>
        /// <response code="404">Plan not found</response>
        /// <response code="401">Unauthorized - missing or invalid token</response>
        [Authorize]
        [HttpDelete("plans/{id}")]
        public async Task<IActionResult> DeleteSchedule(Guid id)
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            var plan = await _db.ScheduledPlans
                .Where(s => s.Id == id && s.UserId == userId.Value)
                .FirstOrDefaultAsync();

            if (plan == null) return NotFound("Schedule plan not found");

            _db.ScheduledPlans.Remove(plan);
            await _db.SaveChangesAsync();

            return Ok(new { success = true, message = "Schedule plan deleted successfully" });
        }

        /// <summary>
        /// Get execution history for a scheduled plan
        /// </summary>
        /// <response code="200">Returns execution history</response>
        /// <response code="404">Plan not found</response>
        /// <response code="401">Unauthorized - missing or invalid token</response>
        [Authorize]
        [HttpGet("plans/{id}/executions")]
        public async Task<IActionResult> GetExecutions(Guid id)
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            var plan = await _db.ScheduledPlans
                .Where(s => s.Id == id && s.UserId == userId.Value)
                .FirstOrDefaultAsync();

            if (plan == null) return NotFound("Schedule plan not found");

            var executions = await _db.ScheduledPlanExecutions
                .Where(e => e.PlanId == id)
                .OrderByDescending(e => e.OccurrenceDate)
                .Take(50)
                .ToListAsync();

            return Ok(executions.Select(e => new
            {
                e.Id,
                planId = e.PlanId,
                e.EntryIndex,
                occurrenceDate = e.OccurrenceDate,
                e.ExecutedAt
            }));
        }
    }
}

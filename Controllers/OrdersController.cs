using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taxi_API.Data;
using Taxi_API.Models;

namespace Taxi_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OrdersController : ControllerBase
    {
        private readonly AppDbContext _db;

        public OrdersController(AppDbContext db)
        {
            _db = db;
        }

        [Authorize]
        [HttpPost("estimate")]
        public IActionResult Estimate([FromBody] Order order)
        {
            // Very simple estimate based on coordinate differences if available
            var distance = 5.0; // km default
            if (order.PickupLat.HasValue && order.DestLat.HasValue && order.PickupLng.HasValue && order.DestLng.HasValue)
            {
                var latDiff = (order.PickupLat.Value - order.DestLat.Value);
                var lngDiff = (order.PickupLng.Value - order.DestLng.Value);
                distance = Math.Sqrt(latDiff * latDiff + lngDiff * lngDiff) * 111; // rough degrees to km
            }

            var price = Math.Max(800m, (decimal)distance * 50m);
            var eta = (int)Math.Ceiling(distance / 0.5); // assume 30 km/h ~ 0.5 km/min

            return Ok(new { distanceKm = Math.Round(distance, 2), price = price, etaMinutes = eta });
        }

        [Authorize]
        [HttpPost("request")]
        public async Task<IActionResult> RequestOrder([FromBody] Order order)
        {
            // create order and start searching for driver
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            order.Id = Guid.NewGuid();
            order.UserId = userId;
            order.CreatedAt = DateTime.UtcNow;
            order.Status = "searching";

            _db.Orders.Add(order);
            await _db.SaveChangesAsync();

            // Simulate driver search: pick first available driver (IsDriver==true)
            var driver = await _db.Users.FirstOrDefaultAsync(u => u.IsDriver && u.DriverProfile != null);
            if (driver != null)
            {
                order.DriverId = driver.Id;
                order.DriverName = driver.Name;
                order.DriverPhone = driver.Phone;
                order.DriverCar = "Toyota";
                order.DriverPlate = "510ZR10";
                order.Status = "assigned";
                order.EtaMinutes = 5;
                order.Price = 800;
                await _db.SaveChangesAsync();

                // In real app, notify driver via push/SMS
            }

            return Ok(order);
        }

        [Authorize]
        [HttpPost("cancel/{id}")]
        public async Task<IActionResult> Cancel(Guid id, [FromBody] string? reason)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();
            order.Status = "cancelled";
            order.CancelledAt = DateTime.UtcNow;
            order.CancelReason = reason;
            await _db.SaveChangesAsync();
            return Ok(order);
        }

        [Authorize]
        [HttpPost("driver/accept/{id}")]
        public async Task<IActionResult> DriverAccept(Guid id)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();
            order.Status = "on_trip";
            await _db.SaveChangesAsync();
            return Ok(order);
        }

        [Authorize]
        [HttpPost("complete/{id}")]
        public async Task<IActionResult> Complete(Guid id)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();
            order.Status = "completed";
            order.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(order);
        }

        [Authorize]
        [HttpPost("rate/{id}")]
        public async Task<IActionResult> Rate(Guid id, [FromBody] Order rating)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();
            order.Rating = rating.Rating;
            order.Review = rating.Review;
            await _db.SaveChangesAsync();
            return Ok(order);
        }
    }
}

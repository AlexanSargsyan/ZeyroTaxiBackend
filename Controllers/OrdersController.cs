using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Linq;
using Taxi_API.Data;
using Taxi_API.Models;
using Taxi_API.DTOs;
using Taxi_API.Services;

namespace Taxi_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OrdersController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ISocketService _socketService;

        public OrdersController(AppDbContext db, ISocketService socketService)
        {
            _db = db;
            _socketService = socketService;
        }

        private static double ToRadians(double deg) => deg * Math.PI / 180.0;

        private static double HaversineDistanceKm(double lat1, double lon1, double lat2, double lon2)
        {
            var R = 6371.0; // Earth radius in km
            var dLat = ToRadians(lat2 - lat1);
            var dLon = ToRadians(lon2 - lon1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private decimal CalculatePrice(double distanceKm, int etaMinutes, double pickupLat, double pickupLng, double destLat, double destLng, string? tariff, bool pet, bool child)
        {
            // Pricing per tariff
            decimal baseFare = tariff == "premium" ? 800m : 400m;
            decimal perKm = tariff == "premium" ? 100m : 60m;
            decimal perMinute = tariff == "premium" ? 30m : 20m;

            var price = baseFare + (decimal)distanceKm * perKm + (decimal)etaMinutes * perMinute;

            // Pet/child surcharge
            if (pet) price += 100m;
            if (child) price += 50m;

            // Example city zone surcharge
            var cityCenterMinLat = 40.15; var cityCenterMaxLat = 40.25;
            var cityCenterMinLng = 44.45; var cityCenterMaxLng = 44.60;

            bool inCityZone = (pickupLat >= cityCenterMinLat && pickupLat <= cityCenterMaxLat && pickupLng >= cityCenterMinLng && pickupLng <= cityCenterMaxLng)
                              || (destLat >= cityCenterMinLat && destLat <= cityCenterMaxLat && destLng >= cityCenterMinLng && destLng <= cityCenterMaxLng);

            if (inCityZone)
            {
                price *= 1.10m; // 10% surcharge
            }

            if (price < 800m) price = 800m;
            return Math.Round(price, 0);
        }

        public record AcceptOrderRequest(double FromLat, double FromLng, Stop[]? Stops, string PaymentMethod, bool Pet, bool Child, string Tariff);
        public record Stop(string Address, double Lat, double Lng);

        [Authorize]
        [HttpPost("accept/{id}")]
        public async Task<IActionResult> AcceptOrder(Guid id, [FromBody] AcceptOrderRequest req)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            // set pickup coords
            order.PickupLat = req.FromLat;
            order.PickupLng = req.FromLng;

            // set stops array as JSON if provided
            if (req.Stops != null && req.Stops.Length > 0)
            {
                order.StopsJson = JsonSerializer.Serialize(req.Stops);
                // set destination as last stop
                var last = req.Stops.Last();
                order.DestLat = last.Lat;
                order.DestLng = last.Lng;
                order.Destination = last.Address;
            }

            order.PaymentMethod = req.PaymentMethod;
            order.PetAllowed = req.Pet;
            order.ChildSeat = req.Child;
            order.Tariff = req.Tariff;

            // compute distance/eta/price now
            if (order.PickupLat.HasValue && order.DestLat.HasValue && order.PickupLng.HasValue && order.DestLng.HasValue)
            {
                var distance = HaversineDistanceKm(order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value);
                var eta = (int)Math.Ceiling(distance / 0.5);
                var price = CalculatePrice(distance, eta, order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value, req.Tariff, req.Pet, req.Child);

                order.DistanceKm = Math.Round(distance, 2);
                order.EtaMinutes = eta;
                order.Price = price;
            }

            // Assign driver simulation
            var driver = await _db.Users.FirstOrDefaultAsync(u => u.IsDriver && u.DriverProfile != null);
            if (driver != null)
            {
                order.DriverId = driver.Id;
                order.DriverName = driver.Name;
                order.DriverPhone = driver.Phone;
                order.DriverCar = "Toyota";
                order.DriverPlate = "510ZR10";
                order.Status = "assigned";
                await _db.SaveChangesAsync();
            }

            await _db.SaveChangesAsync();
            return Ok(order);
        }

        [Authorize]
        [HttpPost("estimate")]
        public IActionResult Estimate([FromBody] Order order)
        {
            // Require coordinates for better estimate
            if (!order.PickupLat.HasValue || !order.DestLat.HasValue || !order.PickupLng.HasValue || !order.DestLng.HasValue)
            {
                return BadRequest("Coordinates required for estimate (PickupLat, PickupLng, DestLat, DestLng).");
            }

            var distance = HaversineDistanceKm(order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value);

            // ETA estimate based on average speed 30 km/h -> 0.5 km/min
            var eta = (int)Math.Ceiling(distance / 0.5);

            var price = CalculatePrice(distance, eta, order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value, order.Tariff, order.PetAllowed, order.ChildSeat);

            return Ok(new { distanceKm = Math.Round(distance, 2), price = price, etaMinutes = eta });
        }

        [Authorize]
        [HttpPost("request")]
        public async Task<IActionResult> RequestOrder([FromBody] Order order)
        {
            // create order and start searching for driver
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            // Validate coordinates
            if (!order.PickupLat.HasValue || !order.DestLat.HasValue || !order.PickupLng.HasValue || !order.DestLng.HasValue)
            {
                return BadRequest("Coordinates required for ordering (PickupLat, PickupLng, DestLat, DestLng).");
            }

            order.Id = Guid.NewGuid();
            order.UserId = userId;
            order.CreatedAt = DateTime.UtcNow;
            order.Status = "searching";

            // Compute distance, eta and price
            var distance = HaversineDistanceKm(order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value);
            var eta = (int)Math.Ceiling(distance / 0.5);
            var price = CalculatePrice(distance, eta, order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value, order.Tariff, order.PetAllowed, order.ChildSeat);

            order.DistanceKm = Math.Round(distance, 2);
            order.EtaMinutes = eta;
            order.Price = price;

            _db.Orders.Add(order);
            await _db.SaveChangesAsync();

            // notify via socket that car finding started
            await _socketService.NotifyOrderEventAsync(order.Id, "carFinding", new { status = "searching" });

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
                // Price remains calculated above
                await _db.SaveChangesAsync();

                // notify assigned
                await _socketService.NotifyOrderEventAsync(order.Id, "carFound", new { driver = new { id = driver.Id, name = driver.Name, phone = driver.Phone } });
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

            // notify both sides
            await _socketService.NotifyOrderEventAsync(order.Id, "cancelUser", new { reason });
            await _socketService.NotifyOrderEventAsync(order.Id, "cancelDriver", new { reason });

            return Ok(order);
        }

        // Endpoint for driver to push location updates for an order
        [Authorize]
        [HttpPost("location/{orderId}")]
        public async Task<IActionResult> UpdateLocation(Guid orderId, [FromBody] LocationUpdate req)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return NotFound();

            // only driver assigned to this order can update location
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();
            if (order.DriverId != userId) return Forbid();

            // Broadcast to rider and driver (if connected)
            await _socketService.BroadcastCarLocationAsync(orderId, req.Lat, req.Lng);
            return Ok(new { ok = true });
        }

        public record LocationUpdate(double Lat, double Lng);

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
        public async Task<IActionResult> Rate(Guid id, [FromBody] RatingRequest req)
        {
            var order = await _db.Orders.Include(o => o.User).FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            // Ensure only the rider who created the order can rate
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();
            if (order.UserId != userId) return Forbid();

            // Only allow rating after completion
            if (order.Status != "completed") return BadRequest("Can rate only completed orders");

            order.Rating = req.Rating;
            order.Review = req.Review;
            await _db.SaveChangesAsync();
            return Ok(order);
        }
    }
}

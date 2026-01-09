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

        private decimal CalculatePrice(double distanceKm, int etaMinutes, double pickupLat, double pickupLng, double destLat, double destLng, string? tariff, string? vehicleType, bool pet, bool child)
        {
            // Pricing based on vehicle type and tariff
            var v = (vehicleType ?? "car").ToLower();

            decimal baseFare;
            decimal perKm;
            decimal perMinute;

            switch (v)
            {
                case "moto":
                    baseFare = 200m;
                    perKm = 30m;
                    perMinute = 8m;
                    break;
                case "van":
                    baseFare = 600m;
                    perKm = 80m;
                    perMinute = 25m;
                    break;
                default: // car
                    baseFare = 400m;
                    perKm = 60m;
                    perMinute = 20m;
                    break;
            }

            // tariff modifiers (e.g., premium)
            if (!string.IsNullOrEmpty(tariff) && tariff.ToLower() == "premium")
            {
                baseFare *= 2m;
                perKm *= 1.5m;
                perMinute *= 1.5m;
            }

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

            // Minimum price depending on vehicle
            decimal minPrice = v == "moto" ? 300m : (v == "van" ? 1000m : 800m);
            if (price < minPrice) price = minPrice;
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
                var price = CalculatePrice(distance, eta, order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value, req.Tariff, order.VehicleType, req.Pet, req.Child);

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
        [HttpPost("estimate/body")]
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

            var price = CalculatePrice(distance, eta, order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value, order.Tariff, order.VehicleType, order.PetAllowed, order.ChildSeat);

            return Ok(new { distanceKm = Math.Round(distance, 2), price = price, etaMinutes = eta });
        }

        // GET estimate via query parameters (supports vehicle type: moto, car, van)
        [Authorize]
        [HttpGet("estimate")]
        public IActionResult EstimateGet([FromQuery] double pickupLat, [FromQuery] double pickupLng, [FromQuery] double destLat, [FromQuery] double destLng, [FromQuery] string? vehicleType, [FromQuery] string? tariff, [FromQuery] bool pet = false, [FromQuery] bool child = false)
        {
            var distance = HaversineDistanceKm(pickupLat, pickupLng, destLat, destLng);
            var eta = (int)Math.Ceiling(distance / 0.5);
            var price = CalculatePrice(distance, eta, pickupLat, pickupLng, destLat, destLng, tariff, vehicleType, pet, child);
            return Ok(new { distanceKm = Math.Round(distance, 2), price = price, etaMinutes = eta, vehicleType = vehicleType ?? "car" });
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

            // If this is a scheduled order in the future, do not connect sockets or search drivers now
            if (order.ScheduledFor.HasValue && order.ScheduledFor.Value > DateTime.UtcNow)
            {
                order.Status = "scheduled";

                // compute distance, eta and price for client convenience
                var distance = HaversineDistanceKm(order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value);
                var eta = (int)Math.Ceiling(distance / 0.5);
                var price = CalculatePrice(distance, eta, order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value, order.Tariff, order.VehicleType, order.PetAllowed, order.ChildSeat);

                order.DistanceKm = Math.Round(distance, 2);
                order.EtaMinutes = eta;
                order.Price = price;

                _db.Orders.Add(order);
                await _db.SaveChangesAsync();

                // Do not notify sockets or assign driver now
                return Ok(order);
            }

            order.Status = "searching";

            // Compute distance, eta and price
            var distanceNow = HaversineDistanceKm(order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value);
            var etaNow = (int)Math.Ceiling(distanceNow / 0.5);
            var priceNow = CalculatePrice(distanceNow, etaNow, order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value, order.Tariff, order.VehicleType, order.PetAllowed, order.ChildSeat);

            order.DistanceKm = Math.Round(distanceNow, 2);
            order.EtaMinutes = etaNow;
            order.Price = priceNow;

            _db.Orders.Add(order);
            await _db.SaveChangesAsync();

            // notify via socket that vehicle finding started (motoFinding/carFinding/vanFinding)
            var vType = (order.VehicleType ?? "car").ToLower();
            var findingEvent = $"{vType}Finding";
            await _socketService.NotifyOrderEventAsync(order.Id, findingEvent, new { status = "searching" });

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
                var foundEvent = $"{vType}Found";
                await _socketService.NotifyOrderEventAsync(order.Id, foundEvent, new { driver = new { id = driver.Id, name = driver.Name, phone = driver.Phone } });
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

        [Authorize]
        [HttpGet("trips")]
        public async Task<IActionResult> GetTrips([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? status = null, [FromQuery] bool asDriver = false)
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;

            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            IQueryable<Order> q = _db.Orders.AsQueryable();
            if (asDriver)
            {
                q = q.Where(o => o.DriverId == userId);
            }
            else
            {
                q = q.Where(o => o.UserId == userId);
            }

            if (!string.IsNullOrEmpty(status))
            {
                q = q.Where(o => o.Status == status);
            }

            var total = await q.CountAsync();

            var items = await q
                .OrderByDescending(o => o.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(o => new
                {
                    id = o.Id,
                    action = o.Action,
                    pickup = o.Pickup,
                    destination = o.Destination,
                    pickupLat = o.PickupLat,
                    pickupLng = o.PickupLng,
                    destLat = o.DestLat,
                    destLng = o.DestLng,
                    stopsJson = o.StopsJson,
                    packageDetails = o.PackageDetails,
                    scheduledFor = o.ScheduledFor,
                    status = o.Status,
                    createdAt = o.CreatedAt,
                    completedAt = o.CompletedAt,
                    cancelledAt = o.CancelledAt,
                    cancelReason = o.CancelReason,
                    driverId = o.DriverId,
                    driverName = o.DriverName,
                    driverPhone = o.DriverPhone,
                    driverCar = o.DriverCar,
                    driverPlate = o.DriverPlate,
                    etaMinutes = o.EtaMinutes,
                    distanceKm = o.DistanceKm,
                    price = o.Price,
                    paymentMethod = o.PaymentMethod,
                    petAllowed = o.PetAllowed,
                    childSeat = o.ChildSeat,
                    tariff = o.Tariff,
                    vehicleType = o.VehicleType,
                    rating = o.Rating,
                    review = o.Review
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, items });
        }

        [Authorize]
        [HttpGet("reviews")]
        public async Task<IActionResult> GetReviews([FromQuery] Guid? driverId = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] int? minRating = null)
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;

            IQueryable<Order> q = _db.Orders.Where(o => o.Rating.HasValue && !string.IsNullOrEmpty(o.Review));

            if (driverId.HasValue)
            {
                q = q.Where(o => o.DriverId == driverId.Value);
            }

            if (minRating.HasValue)
            {
                q = q.Where(o => o.Rating >= minRating.Value);
            }

            var total = await q.CountAsync();

            var items = await q
                .OrderByDescending(o => o.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(o => new
                {
                    orderId = o.Id,
                    rating = o.Rating,
                    review = o.Review,
                    createdAt = o.CreatedAt,
                    driverId = o.DriverId,
                    driverName = o.DriverName,
                    userId = o.UserId,
                    userName = o.User != null ? o.User.Name : null,
                    price = o.Price,
                    vehicleType = o.VehicleType
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, items });
        }
    }
}

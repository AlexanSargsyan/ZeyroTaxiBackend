using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Taxi_API.Data;
using Taxi_API.Models;
using Taxi_API.DTOs;
using Taxi_API.Services;

namespace Taxi_API.Controllers
{
    /// <summary>
    /// Location update model for driver location tracking
    /// </summary>
    public record LocationUpdate(double Lat, double Lng);

    /// <summary>
    /// Destination stop with address and coordinates
    /// </summary>
    public record DestinationStop(string Address, double Lat, double Lng);
    
    /// <summary>
    /// Request model for creating/accepting orders
    /// </summary>
    public record CreateOrderRequest(
        double FromLat,
        double FromLng,
        DestinationStop[] To,
        string PaymentMethod,
        bool Pet,
        bool Child,
        string Tariff,
        string? VehicleType = "car"
    );

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

        private Guid? GetUserIdFromClaims()
        {
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                         ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                         ?? User.FindFirst("sub")?.Value;
            
            if (string.IsNullOrEmpty(userIdStr)) return null;
            return Guid.TryParse(userIdStr, out var userId) ? userId : null;
        }

        private static double ToRadians(double deg) => deg * Math.PI / 180.0;

        private static double HaversineDistanceKm(double lat1, double lon1, double lat2, double lon2)
        {
            var R = 6371.0;
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
            var v = (vehicleType ?? "car").ToLower();
            decimal baseFare, perKm, perMinute;

            switch (v)
            {
                case "moto": baseFare = 200m; perKm = 30m; perMinute = 8m; break;
                case "van": baseFare = 600m; perKm = 80m; perMinute = 25m; break;
                default: baseFare = 400m; perKm = 60m; perMinute = 20m; break;
            }

            if (!string.IsNullOrEmpty(tariff) && tariff.ToLower() == "premium")
            {
                baseFare *= 2m;
                perKm *= 1.5m;
                perMinute *= 1.5m;
            }

            var price = baseFare + (decimal)distanceKm * perKm + (decimal)etaMinutes * perMinute;
            if (pet) price += 100m;
            if (child) price += 50m;

            var cityCenterMinLat = 40.15; var cityCenterMaxLat = 40.25;
            var cityCenterMinLng = 44.45; var cityCenterMaxLng = 44.60;
            bool inCityZone = (pickupLat >= cityCenterMinLat && pickupLat <= cityCenterMaxLat && pickupLng >= cityCenterMinLng && pickupLng <= cityCenterMaxLng)
                              || (destLat >= cityCenterMinLat && destLat <= cityCenterMaxLat && destLng >= cityCenterMinLng && destLng <= cityCenterMaxLng);
            if (inCityZone) price *= 1.10m;

            decimal minPrice = v == "moto" ? 300m : (v == "van" ? 1000m : 800m);
            if (price < minPrice) price = minPrice;
            return Math.Round(price, 0);
        }

        /// <summary>
        /// Create a new taxi order with automatic driver assignment
        /// </summary>
        /// <remarks>
        /// **Main endpoint for clients to request a ride.**
        /// 
        /// Creates a complete order with all details, calculates pricing,
        /// and automatically assigns an available driver.
        /// 
        /// **Request Body Parameters:**
        /// - **fromLat** (double, required): Pickup latitude coordinate
        /// - **fromLng** (double, required): Pickup longitude coordinate
        /// - **to** (array, required: Array of destination stops with address, lat, lng
        /// - **paymentMethod** (string, required): Payment method - "cash" or "card"
        /// - **pet** (boolean, required): Pet allowed (+100 AMD surcharge)
        /// - **child** (boolean, required): Child seat required (+50 AMD surcharge)
        /// - **tariff** (string, required): Tariff type - "standard" or "premium"
        /// - **vehicleType** (string, optional): Vehicle type - "car", "moto", or "van" (default: "car")
        /// 
        /// **Request Example:**
        /// ```json
        /// {
        ///   "fromLat": 40.1872,
        ///   "fromLng": 44.5152,
        ///   "to": [
        ///     {
        ///       "address": "Republic Square, Yerevan",
        ///       "lat": 40.1776,
        ///       "lng": 44.5126
        ///     }
        ///   ],
        ///   "paymentMethod": "cash",
        ///   "pet": false,
        ///   "child": true,
        ///   "tariff": "standard",
        ///   "vehicleType": "car"
        /// }
        /// ```
        /// 
        /// **Response:**
        /// Returns the created order with calculated price, distance, ETA, and assigned driver info.
        /// </remarks>
        /// <param name="request">Order creation request with pickup/destination, payment, and preferences</param>
        /// <response code="200">Order created successfully with driver assigned</response>
        /// <response code="400">Invalid request parameters</response>
        /// <response code="401">Unauthorized - invalid or missing authentication token</response>
        [Authorize]
        [HttpPost("create-order")]
        [ProducesResponseType(typeof(Order), 200)]
        [ProducesResponseType(typeof(object), 400)]
        public async Task<IActionResult> CreateOrder([FromBody] CreateOrderRequest request)
        {
            if (request == null) return BadRequest("Request body is required");
            if (request.To == null || request.To.Length == 0) return BadRequest("At least one destination is required");
            if (string.IsNullOrWhiteSpace(request.PaymentMethod)) return BadRequest("Payment method is required");
            if (request.PaymentMethod.ToLower() != "cash" && request.PaymentMethod.ToLower() != "card")
                return BadRequest("Payment method must be 'cash' or 'card'");
            if (string.IsNullOrWhiteSpace(request.Tariff)) return BadRequest("Tariff is required");

            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            // Verify user exists in database
            var user = await _db.Users.FindAsync(userId.Value);
            if (user == null) return Unauthorized("User not found in database. Please re-authenticate.");

            var vehicleType = (request.VehicleType ?? "car").ToLower();
            if (vehicleType != "car" && vehicleType != "moto" && vehicleType != "van")
                return BadRequest("Vehicle type must be 'car', 'moto', or 'van'");

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId.Value,
                CreatedAt = DateTime.UtcNow,
                Status = "searching",
                Action = "ride",
                PickupLat = request.FromLat,
                PickupLng = request.FromLng,
                Pickup = $"Lat: {request.FromLat}, Lng: {request.FromLng}",
                PaymentMethod = request.PaymentMethod.ToLower(),
                PetAllowed = request.Pet,
                ChildSeat = request.Child,
                Tariff = request.Tariff,
                VehicleType = vehicleType
            };

            if (request.To.Length == 1)
            {
                var dest = request.To[0];
                order.DestLat = dest.Lat;
                order.DestLng = dest.Lng;
                order.Destination = dest.Address;
            }
            else
            {
                order.StopsJson = JsonSerializer.Serialize(request.To.Select(s => new { address = s.Address, lat = s.Lat, lng = s.Lng }));
                var lastStop = request.To.Last();
                order.DestLat = lastStop.Lat;
                order.DestLng = lastStop.Lng;
                order.Destination = lastStop.Address;
            }

            double totalDistance = 0;
            double currentLat = request.FromLat;
            double currentLng = request.FromLng;

            foreach (var stop in request.To)
            {
                totalDistance += HaversineDistanceKm(currentLat, currentLng, stop.Lat, stop.Lng);
                currentLat = stop.Lat;
                currentLng = stop.Lng;
            }

            var eta = (int)Math.Ceiling(totalDistance / 0.5);
            var finalDestination = request.To.Last();
            var price = CalculatePrice(totalDistance, eta, request.FromLat, request.FromLng, 
                finalDestination.Lat, finalDestination.Lng, request.Tariff, order.VehicleType, request.Pet, request.Child);

            order.DistanceKm = Math.Round(totalDistance, 2);
            order.EtaMinutes = eta;
            order.Price = price;

            _db.Orders.Add(order);
            await _db.SaveChangesAsync();

            await _socketService.NotifyOrderEventAsync(order.Id, $"{vehicleType}Finding", new { status = "searching" });

            var driver = await _db.Users.FirstOrDefaultAsync(u => u.IsDriver && u.DriverProfile != null);
            if (driver != null)
            {
                order.DriverId = driver.Id;
                order.DriverName = driver.Name;
                order.DriverPhone = driver.Phone;
                
                var driverProfile = await _db.DriverProfiles.FirstOrDefaultAsync(dp => dp.UserId == driver.Id);
                if (driverProfile != null)
                {
                    order.DriverCar = $"{driverProfile.CarMake} {driverProfile.CarModel}".Trim();
                    order.DriverPlate = driverProfile.CarPlate;
                }
                else
                {
                    order.DriverCar = "Toyota";
                    order.DriverPlate = "510ZR10";
                }
                
                order.Status = "assigned";
                order.EtaMinutes = 5;
                await _db.SaveChangesAsync();

                await _socketService.NotifyOrderEventAsync(order.Id, $"{vehicleType}Found", new 
                { 
                    driver = new { id = driver.Id, name = driver.Name, phone = driver.Phone, car = order.DriverCar, plate = order.DriverPlate } 
                });
            }

            // Return DTO without navigation properties to avoid circular reference
            return Ok(new
            {
                order.Id,
                order.Action,
                order.UserId,
                order.Pickup,
                order.Destination,
                order.PickupLat,
                order.PickupLng,
                order.DestLat,
                order.DestLng,
                order.StopsJson,
                order.Status,
                order.CreatedAt,
                order.DriverId,
                order.DriverName,
                order.DriverPhone,
                order.DriverCar,
                order.DriverPlate,
                order.EtaMinutes,
                order.DistanceKm,
                order.Price,
                order.PaymentMethod,
                order.PetAllowed,
                order.ChildSeat,
                order.Tariff,
                order.VehicleType
            });
        }

        /// <summary>
        /// Accept and create order as driver (DRIVER endpoint)
        /// </summary>
        /// <remarks>
        /// **Endpoint for drivers to manually create/accept an order.**
        /// 
        /// Used when a driver receives an order request via phone or manually creates
        /// an order for a client. The order is immediately assigned to the driver.
        /// 
        /// **Request Body Parameters:**
        /// - **fromLat** (double, required): Pickup latitude coordinate
        /// - **fromLng** (double, required): Pickup longitude coordinate
        /// - **to** (array, required): Array of destination stops with address, lat, lng
        /// - **paymentMethod** (string, required): Payment method - "cash" or "card"
        /// - **pet** (boolean, required): Pet allowed (+100 AMD surcharge)
        /// - **child** (boolean, required): Child seat required (+50 AMD surcharge)
        /// - **tariff** (string, required): Tariff type - "standard" or "premium"
        /// - **vehicleType** (string, optional: Vehicle type - "car", "moto", or "van" (default: "car")
        /// 
        /// **Request Example:**
        /// ```json
        /// {
        ///   "fromLat": 40.1872,
        ///   "fromLng": 44.5152,
        ///   "to": [
        ///     {
        ///       "address": "Republic Square, Yerevan",
        ///       "lat": 40.1776,
        ///       "lng": 44.5126
        ///     }
        ///   ],
        ///   "paymentMethod": "cash",
        ///   "pet": false,
        ///   "child": true,
        ///   "tariff": "standard",
        ///   "vehicleType": "car"
        /// }
        /// ```
        /// 
        /// **Differences from create-order:**
        /// - Only drivers can use this endpoint (verified by IsDriver flag)
        /// - Order status is set to "assigned" immediately (not "searching")
        /// - Driver is automatically assigned to themselves
        /// 
        /// **Response:**
        /// Returns the created order with calculated price, distance, ETA, and driver info.
        /// </remarks>
        /// <param name="request">Order acceptance request with pickup/destination, payment, and preferences</param>
        /// <response code="200">Order accepted successfully and assigned to driver</response>
        /// <response code="400">Invalid request parameters or user is not a driver</response>
        /// <response code="401">Unauthorized - invalid or missing authentication token</response>
        [Authorize]
        [HttpPost("accept-order")]
        [ProducesResponseType(typeof(Order), 200)]
        [ProducesResponseType(typeof(object), 400)]
        public async Task<IActionResult> AcceptOrder([FromBody] CreateOrderRequest request)
        {
            if (request == null) return BadRequest("Request body is required");
            if (request.To == null || request.To.Length == 0) return BadRequest("At least one destination is required");
            if (string.IsNullOrWhiteSpace(request.PaymentMethod)) return BadRequest("Payment method is required");
            if (request.PaymentMethod.ToLower() != "cash" && request.PaymentMethod.ToLower() != "card")
                return BadRequest("Payment method must be 'cash' or 'card'");
            if (string.IsNullOrWhiteSpace(request.Tariff)) return BadRequest("Tariff is required");

            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            var driver = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value && u.IsDriver);
            if (driver == null) return BadRequest(new { error = "Only drivers can accept orders or user not found in database" });

            var vehicleType = (request.VehicleType ?? "car").ToLower();
            if (vehicleType != "car" && vehicleType != "moto" && vehicleType != "van")
                return BadRequest("Vehicle type must be 'car', 'moto', or 'van'");

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId.Value,
                CreatedAt = DateTime.UtcNow,
                Status = "assigned",
                Action = "ride",
                PickupLat = request.FromLat,
                PickupLng = request.FromLng,
                Pickup = $"Lat: {request.FromLat}, Lng: {request.FromLng}",
                PaymentMethod = request.PaymentMethod.ToLower(),
                PetAllowed = request.Pet,
                ChildSeat = request.Child,
                Tariff = request.Tariff,
                VehicleType = vehicleType,
                DriverId = userId.Value,
                DriverName = driver.Name,
                DriverPhone = driver.Phone
            };

            if (request.To.Length == 1)
            {
                var dest = request.To[0];
                order.DestLat = dest.Lat;
                order.DestLng = dest.Lng;
                order.Destination = dest.Address;
            }
            else
            {
                order.StopsJson = JsonSerializer.Serialize(request.To.Select(s => new { address = s.Address, lat = s.Lat, lng = s.Lng }));
                var lastStop = request.To.Last();
                order.DestLat = lastStop.Lat;
                order.DestLng = lastStop.Lng;
                order.Destination = lastStop.Address;
            }

            double totalDistance = 0;
            double currentLat = request.FromLat;
            double currentLng = request.FromLng;

            foreach (var stop in request.To)
            {
                totalDistance += HaversineDistanceKm(currentLat, currentLng, stop.Lat, stop.Lng);
                currentLat = stop.Lat;
                currentLng = stop.Lng;
            }

            var eta = (int)Math.Ceiling(totalDistance / 0.5);
            var finalDestination = request.To.Last();
            var price = CalculatePrice(totalDistance, eta, request.FromLat, request.FromLng, 
                finalDestination.Lat, finalDestination.Lng, request.Tariff, order.VehicleType, request.Pet, request.Child);

            order.DistanceKm = Math.Round(totalDistance, 2);
            order.EtaMinutes = eta;
            order.Price = price;

            var driverProfile = await _db.DriverProfiles.FirstOrDefaultAsync(dp => dp.UserId == userId.Value);
            if (driverProfile != null)
            {
                order.DriverCar = $"{driverProfile.CarMake} {driverProfile.CarModel}".Trim();
                order.DriverPlate = driverProfile.CarPlate;
            }
            else
            {
                order.DriverCar = "Toyota";
                order.DriverPlate = "510ZR10";
            }

            _db.Orders.Add(order);
            await _db.SaveChangesAsync();

            // Return DTO without navigation properties to avoid circular reference
            return Ok(new
            {
                order.Id,
                order.Action,
                order.UserId,
                order.Pickup,
                order.Destination,
                order.PickupLat,
                order.PickupLng,
                order.DestLat,
                order.DestLng,
                order.StopsJson,
                order.Status,
                order.CreatedAt,
                order.DriverId,
                order.DriverName,
                order.DriverPhone,
                order.DriverCar,
                order.DriverPlate,
                order.EtaMinutes,
                order.DistanceKm,
                order.Price,
                order.PaymentMethod,
                order.PetAllowed,
                order.ChildSeat,
                order.Tariff,
                order.VehicleType
            });
        }

        /// <summary>
        /// Request a new order (legacy endpoint)
        /// </summary>
        [Authorize]
        [HttpPost("request")]
        public async Task<IActionResult> RequestOrder([FromBody] Order order)
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            if (!order.PickupLat.HasValue || !order.DestLat.HasValue || !order.PickupLng.HasValue || !order.DestLng.HasValue)
                return BadRequest("Coordinates required for ordering (PickupLat, PickupLng, DestLat, DestLng).");

            order.Id = Guid.NewGuid();
            order.UserId = userId.Value;
            order.CreatedAt = DateTime.UtcNow;

            if (order.ScheduledFor.HasValue && order.ScheduledFor.Value > DateTime.UtcNow)
            {
                order.Status = "scheduled";
                var distance = HaversineDistanceKm(order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value);
                var eta = (int)Math.Ceiling(distance / 0.5);
                var price = CalculatePrice(distance, eta, order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value, order.Tariff, order.VehicleType, order.PetAllowed, order.ChildSeat);

                order.DistanceKm = Math.Round(distance, 2);
                order.EtaMinutes = eta;
                order.Price = price;

                _db.Orders.Add(order);
                await _db.SaveChangesAsync();
                return Ok(order);
            }

            order.Status = "searching";
            var distanceNow = HaversineDistanceKm(order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value);
            var etaNow = (int)Math.Ceiling(distanceNow / 0.5);
            var priceNow = CalculatePrice(distanceNow, etaNow, order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value, order.Tariff, order.VehicleType, order.PetAllowed, order.ChildSeat);

            order.DistanceKm = Math.Round(distanceNow, 2);
            order.EtaMinutes = etaNow;
            order.Price = priceNow;

            _db.Orders.Add(order);
            await _db.SaveChangesAsync();

            var vType = (order.VehicleType ?? "car").ToLower();
            await _socketService.NotifyOrderEventAsync(order.Id, $"{vType}Finding", new { status = "searching" });

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
                await _db.SaveChangesAsync();

                await _socketService.NotifyOrderEventAsync(order.Id, $"{vType}Found", new { driver = new { id = driver.Id, name = driver.Name, phone = driver.Phone } });
            }

            return Ok(order);
        }

        /// <summary>
        /// Get ride estimate with order details
        /// </summary>
        [Authorize]
        [HttpPost("estimate/body")]
        public IActionResult Estimate([FromBody] Order order)
        {
            if (!order.PickupLat.HasValue || !order.DestLat.HasValue || !order.PickupLng.HasValue || !order.DestLng.HasValue)
                return BadRequest("Coordinates required for estimate (PickupLat, PickupLng, DestLat, DestLng).");

            var distance = HaversineDistanceKm(order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value);
            var eta = (int)Math.Ceiling(distance / 0.5);
            var price = CalculatePrice(distance, eta, order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value, order.Tariff, order.VehicleType, order.PetAllowed, order.ChildSeat);

            return Ok(new { distanceKm = Math.Round(distance, 2), price, etaMinutes = eta });
        }

        /// <summary>
        /// Get ride estimate by coordinates
        /// </summary>
        [Authorize]
        [HttpGet("estimate")]
        public IActionResult EstimateGet([FromQuery] double pickupLat, [FromQuery] double pickupLng, [FromQuery] double destLat, [FromQuery] double destLng, [FromQuery] string? vehicleType, [FromQuery] string? tariff, [FromQuery] bool pet = false, [FromQuery] bool child = false)
        {
            var distance = HaversineDistanceKm(pickupLat, pickupLng, destLat, destLng);
            var eta = (int)Math.Ceiling(distance / 0.5);
            var price = CalculatePrice(distance, eta, pickupLat, pickupLng, destLat, destLng, tariff, vehicleType, pet, child);
            return Ok(new { distanceKm = Math.Round(distance, 2), price, etaMinutes = eta, vehicleType = vehicleType ?? "car" });
        }

        /// <summary>
        /// Cancel an order with optional reason
        /// </summary>
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

            await _socketService.NotifyOrderEventAsync(order.Id, "cancelUser", new { reason });
            await _socketService.NotifyOrderEventAsync(order.Id, "cancelDriver", new { reason });

            return Ok(order);
        }

        /// <summary>
        /// Update driver location during trip
        /// </summary>
        [Authorize]
        [HttpPost("location/{orderId}")]
        public async Task<IActionResult> UpdateLocation(Guid orderId, [FromBody] LocationUpdate req)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return NotFound();

            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");
            if (order.DriverId != userId.Value) return Forbid();

            await _socketService.BroadcastCarLocationAsync(orderId, req.Lat, req.Lng);
            return Ok(new { ok = true });
        }

        /// <summary>
        /// Start trip as driver
        /// </summary>
        [Authorize]
        [HttpPost("driver/start-trip/{id}")]
        public async Task<IActionResult> DriverStartTrip(Guid id)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();
            
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized();
            if (order.DriverId != userId.Value) return Forbid();

            order.Status = "on_trip";
            await _db.SaveChangesAsync();
            return Ok(order);
        }

        /// <summary>
        /// Mark order as completed
        /// </summary>
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

        /// <summary>
        /// Rate a completed order
        /// </summary>
        [Authorize]
        [HttpPost("rate/{id}")]
        public async Task<IActionResult> Rate(Guid id, [FromBody] RatingRequest req)
        {
            var order = await _db.Orders.Include(o => o.User).FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");
            if (order.UserId != userId.Value) return Forbid();
            if (order.Status != "completed") return BadRequest("Can rate only completed orders");

            order.Rating = req.Rating;
            order.Review = req.Review;
            await _db.SaveChangesAsync();
            return Ok(order);
        }

        /// <summary>
        /// Get user or driver trip history
        /// </summary>
        [Authorize]
        [HttpGet("trips")]
        public async Task<IActionResult> GetTrips([FromQuery] string? status = null, [FromQuery] bool asDriver = false)
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            IQueryable<Order> q = _db.Orders.AsQueryable();
            if (asDriver) q = q.Where(o => o.DriverId == userId.Value);
            else q = q.Where(o => o.UserId == userId.Value);

            if (!string.IsNullOrEmpty(status)) q = q.Where(o => o.Status == status);

            var items = await q.OrderByDescending(o => o.CreatedAt)
                .Select(o => new
                {
                    o.Id, o.Action, o.Pickup, o.Destination, o.PickupLat, o.PickupLng, o.DestLat, o.DestLng,
                    o.StopsJson, o.PackageDetails, o.ScheduledFor, o.Status, o.CreatedAt, o.CompletedAt,
                    o.CancelledAt, o.CancelReason, o.DriverId, o.DriverName, o.DriverPhone, o.DriverCar,
                    o.DriverPlate, o.EtaMinutes, o.DistanceKm, o.Price, o.PaymentMethod, o.PetAllowed,
                    o.ChildSeat, o.Tariff, o.VehicleType, o.Rating, o.Review
                }).ToListAsync();

            return Ok(items);
        }

        /// <summary>
        /// Get reviews with optional filters
        /// </summary>
        [Authorize]
        [HttpGet("reviews")]
        public async Task<IActionResult> GetReviews([FromQuery] Guid? driverId = null, [FromQuery] int? minRating = null)
        {
            IQueryable<Order> q = _db.Orders.Where(o => o.Rating.HasValue && !string.IsNullOrEmpty(o.Review));
            if (driverId.HasValue) q = q.Where(o => o.DriverId == driverId.Value);
            if (minRating.HasValue) q = q.Where(o => o.Rating >= minRating.Value);

            var items = await q.OrderByDescending(o => o.CreatedAt)
                .Select(o => new
                {
                    o.Id, o.Rating, o.Review, o.CreatedAt, o.DriverId, o.DriverName, o.UserId,
                    userName = o.User != null ? o.User.Name : null, o.Price, o.VehicleType
                }).ToListAsync();

            return Ok(items);
        }

        /// <summary>
        /// Driver receives and accepts order
        /// </summary>
        [Authorize]
        [HttpPost("map/receive/{id}")]
        public async Task<IActionResult> MapReceive(Guid id)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            order.DriverId = userId.Value;
            var driver = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value);
            if (driver != null)
            {
                order.DriverName = driver.Name;
                order.DriverPhone = driver.Phone;
            }
            order.Status = "assigned";
            await _db.SaveChangesAsync();

            await _socketService.NotifyOrderEventAsync(order.Id, "receiveOrder", new { driverId = userId.Value, driverName = order.DriverName });
            return Ok(order);
        }

        /// <summary>
        /// Driver arrives at pickup location
        /// </summary>
        [Authorize]
        [HttpPost("map/arrive/{id}")]
        public async Task<IActionResult> MapArrive(Guid id)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");
            if (order.DriverId != userId.Value) return Forbid();

            await _socketService.NotifyOrderEventAsync(order.Id, "arrive", new { driverId = userId.Value, driverName = order.DriverName });
            return Ok(new { ok = true });
        }

        /// <summary>
        /// Driver starts the trip
        /// </summary>
        [Authorize]
        [HttpPost("map/start/{id}")]
        public async Task<IActionResult> MapStart(Guid id)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");
            if (order.DriverId != userId.Value) return Forbid();

            order.Status = "on_trip";
            await _db.SaveChangesAsync();

            await _socketService.NotifyOrderEventAsync(order.Id, "start", new { driverId = userId.Value, driverName = order.DriverName });
            return Ok(order);
        }

        /// <summary>
        /// Driver completes the trip
        /// </summary>
        [Authorize]
        [HttpPost("map/complete/{id}")]
        public async Task<IActionResult> MapComplete(Guid id)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");
            if (order.DriverId != userId.Value) return Forbid();

            order.Status = "completed";
            order.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await _socketService.NotifyOrderEventAsync(order.Id, "complete", new { driverId = userId.Value, driverName = order.DriverName });
            return Ok(order);
        }

        /// <summary>
        /// Cancel trip by driver or user
        /// </summary>
        [Authorize]
        [HttpPost("map/cancel/{id}")]
        public async Task<IActionResult> MapCancel(Guid id, [FromBody] string? reason)
        {
            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            if (order.UserId != userId.Value && order.DriverId != userId.Value) return Forbid();

            order.Status = "cancelled";
            order.CancelledAt = DateTime.UtcNow;
            order.CancelReason = reason;
            await _db.SaveChangesAsync();

            await _socketService.NotifyOrderEventAsync(order.Id, "cancelOrder", new { by = userId.Value, reason });
            return Ok(order);
        }
    }
}

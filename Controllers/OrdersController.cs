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
    /// Request model for creating/accepting orders with scheduled time support
    /// </summary>
    public record CreateOrderRequest(
        double FromLat,
        double FromLng,
        DestinationStop[] To,
        string PaymentMethod,
        bool Pet,
        bool Child,
        string Tariff,
        DateTime? RequestedTime  // When the taxi is needed (null = immediate, future = scheduled)
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
            decimal baseFare, perKm, perMinute;

            // If vehicleType is provided, it's a delivery order (moto, car, van)
            if (!string.IsNullOrEmpty(vehicleType))
            {
                var v = vehicleType.ToLower();
                switch (v)
                {
                    case "moto": 
                        baseFare = 200m; perKm = 30m; perMinute = 8m; 
                        break;
                    case "van": 
                        baseFare = 600m; perKm = 80m; perMinute = 25m; 
                        break;
                    case "car":
                    default: 
                        baseFare = 400m; perKm = 60m; perMinute = 20m; 
                        break;
                }

                var price = baseFare + (decimal)distanceKm * perKm + (decimal)etaMinutes * perMinute;
                if (pet) price += 100m;
                if (child) price += 50m;

                // City center surcharge
                var cityCenterMinLat = 40.15; var cityCenterMaxLat = 40.25;
                var cityCenterMinLng = 44.45; var cityCenterMaxLng = 44.60;
                bool inCityZone = (pickupLat >= cityCenterMinLat && pickupLat <= cityCenterMaxLat && pickupLng >= cityCenterMinLng && pickupLng <= cityCenterMaxLng)
                                  || (destLat >= cityCenterMinLat && destLat <= cityCenterMaxLat && destLng >= cityCenterMinLng && destLng <= cityCenterMaxLng);
                if (inCityZone) price *= 1.10m;

                // Minimum prices for delivery
                decimal minPrice = v == "moto" ? 300m : (v == "van" ? 1000m : 800m);
                if (price < minPrice) price = minPrice;
                
                return Math.Round(price, 0);
            }
            else
            {
                // Taxi order - use tariff-based pricing
                baseFare = 400m;
                perKm = 60m;
                perMinute = 20m;

                // Apply tariff surcharges for taxi orders
                if (!string.IsNullOrEmpty(tariff))
                {
                    switch (tariff.ToLower())
                    {
                        case "electro":
                        case "standard":
                            baseFare += 100m; // Standard (Electro) +100 AMD
                            break;
                        case "economy":
                        case "start":
                            baseFare += 200m; // Start (Economy) +200 AMD
                            break;
                        case "comfort":
                            baseFare += 300m; // Comfort +300 AMD
                            break;
                        case "business":
                            baseFare += 400m; // Business +400 AMD
                            break;
                        case "premium":
                            baseFare += 500m; // Premium +500 AMD
                            break;
                    }
                }

                var price = baseFare + (decimal)distanceKm * perKm + (decimal)etaMinutes * perMinute;
                
                // Add surcharges for pet and child
                if (pet) price += 100m;
                if (child) price += 50m;

                // City center surcharge (10%)
                var cityCenterMinLat = 40.15; var cityCenterMaxLat = 40.25;
                var cityCenterMinLng = 44.45; var cityCenterMaxLng = 44.60;
                bool inCityZone = (pickupLat >= cityCenterMinLat && pickupLat <= cityCenterMaxLat && pickupLng >= cityCenterMinLng && pickupLng <= cityCenterMaxLng)
                                  || (destLat >= cityCenterMinLat && destLat <= cityCenterMaxLat && destLng >= cityCenterMinLng && destLng <= cityCenterMaxLng);
                if (inCityZone) price *= 1.10m;

                // Minimum prices per tariff
                decimal minPrice = 800m; // Base minimum
                if (!string.IsNullOrEmpty(tariff))
                {
                    switch (tariff.ToLower())
                    {
                        case "electro":
                        case "standard":
                            minPrice = 300m;
                            break;
                        case "economy":
                        case "start":
                            minPrice = 800m;
                            break;
                        case "comfort":
                            minPrice = 1000m;
                            break;
                        case "business":
                            minPrice = 1500m;
                            break;
                        case "premium":
                            minPrice = 2000m;
                            break;
                    }
                }
                
                if (price < minPrice) price = minPrice;
                return Math.Round(price, 0);
            }
        }

        /// <summary>
        /// Create a new taxi order with automatic driver assignment
        /// </summary>
        /// <remarks>
        /// **Main endpoint for clients to request a ride.**
        /// 
        /// Creates a complete order with all details, calculates pricing,
        /// and automatically assigns an available driver (immediate) or schedules for later.
        /// 
        /// **Request Body Parameters:**
        /// - **fromLat** (double, required): Pickup latitude coordinate
        /// - **fromLng** (double, required): Pickup longitude coordinate
        /// - **to** (array, required): Array of destination stops with address, lat, lng
        /// - **paymentMethod** (string, required): Payment method - "cash" or "card"
        /// - **pet** (boolean, required): Pet allowed (+100 AMD surcharge)
        /// - **child** (boolean, required): Child seat required (+50 AMD surcharge)
        /// - **tariff** (string, required): Tariff type - "electro", "economy", "comfort", "business", or "premium"
        /// - **requestedTime** (datetime, optional): When the taxi is needed. If null or past, dispatch immediately. If future, schedule for that time.
        /// 
        /// **Scheduling Behavior:**
        /// - **Immediate Ride** (requestedTime = null or past): Status = "searching", driver assigned immediately
        /// - **Scheduled Ride** (requestedTime = future): Status = "scheduled", driver assigned ~5 minutes before requestedTime
        /// 
        /// **Request Example (Immediate):**
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
        ///   "tariff": "economy",
        ///   "requestedTime": null
        /// }
        /// ```
        /// 
        /// **Request Example (Scheduled for 2 hours from now):**
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
        ///   "child": false,
        ///   "tariff": "comfort",
        ///   "requestedTime": "2026-01-24T14:30:00Z"
        /// }
        /// ```
        /// 
        /// **Response:**
        /// Returns the created order with calculated price, distance, ETA, and assigned driver info (if immediate).
        /// </remarks>
        /// <param name="request">Order creation request with pickup/destination, payment, preferences, and requested time</param>
        /// <response code="200">Order created successfully</response>
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

            // Determine if this is a scheduled ride (requestedTime is in the future)
            bool isScheduled = request.RequestedTime.HasValue && request.RequestedTime.Value > DateTime.UtcNow.AddMinutes(5);
            
            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId.Value,
                CreatedAt = DateTime.UtcNow,
                Status = isScheduled ? "scheduled" : "searching",
                Action = "ride",
                PickupLat = request.FromLat,
                PickupLng = request.FromLng,
                Pickup = $"Lat: {request.FromLat}, Lng: {request.FromLng}",
                PaymentMethod = request.PaymentMethod.ToLower(),
                PetAllowed = request.Pet,
                ChildSeat = request.Child,
                Tariff = request.Tariff,
                VehicleType = null, // No vehicle type for taxi orders
                ScheduledFor = request.RequestedTime // Store the requested time
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
                finalDestination.Lat, finalDestination.Lng, request.Tariff, null, request.Pet, request.Child);

            order.DistanceKm = Math.Round(totalDistance, 2);
            order.EtaMinutes = eta;
            order.Price = price;

            _db.Orders.Add(order);
            await _db.SaveChangesAsync();

            // Handle immediate vs scheduled dispatch
            if (isScheduled)
            {
                // Scheduled ride - do not assign driver yet
                // The ScheduledPlanProcessor background service will handle driver assignment
                // approximately 5-10 minutes before the scheduled time
                await _socketService.NotifyOrderEventAsync(order.Id, "rideScheduled", new 
                { 
                    status = "scheduled", 
                    scheduledFor = order.ScheduledFor,
                    message = $"Ride scheduled for {order.ScheduledFor:yyyy-MM-dd HH:mm}" 
                });
            }
            else
            {
                // Immediate ride - start searching for driver immediately
                await _socketService.NotifyOrderEventAsync(order.Id, "taxiFinding", new { status = "searching" });

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

                    await _socketService.NotifyOrderEventAsync(order.Id, "taxiFound", new 
                    { 
                        driver = new { id = driver.Id, name = driver.Name, phone = driver.Phone, car = order.DriverCar, plate = order.DriverPlate } 
                    });
                }
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
                order.ScheduledFor,
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
                order.Tariff
            });
        }

        /// <summary>
        /// Accept and create order as driver (DRIVER endpoint)
        /// </summary>
        /// <remarks>
        /// **Endpoint for drivers to manually create/accept an order.**
        /// 
        /// Used when a driver receives an order request via phone or manually creates
        /// an order for a client. The order can be immediate or scheduled for a future time.
        /// 
        /// **Request Body Parameters:**
        /// - **fromLat** (double, required): Pickup latitude coordinate
        /// - **fromLng** (double, required): Pickup longitude coordinate
        /// - **to** (array, required): Array of destination stops with address, lat, lng
        /// - **paymentMethod** (string, required): Payment method - "cash" or "card"
        /// - **pet** (boolean, required): Pet allowed (+100 AMD surcharge)
        /// - **child** (boolean, required): Child seat required (+50 AMD surcharge)
        /// - **tariff** (string, required): Tariff type - "electro", "economy", "comfort", "business", or "premium"
        /// - **requestedTime** (datetime, optional): When the taxi is needed. If null or past, immediate. If future, scheduled.
        /// 
        /// **Request Example (Immediate):**
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
        ///   "tariff": "economy",
        ///   "requestedTime": null
        /// }
        /// ```
        /// 
        /// **Request Example (Scheduled):**
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
        ///   "child": false,
        ///   "tariff": "comfort",
        ///   "requestedTime": "2026-01-24T16:00:00Z"
        /// }
        /// ```
        /// 
        /// **Differences from create-order:**
        /// - Only drivers can use this endpoint (verified by IsDriver flag)
        /// - For immediate orders: Status = "assigned" immediately (driver is assigned to themselves)
        /// - For scheduled orders: Status = "scheduled", driver pre-assigned to themselves
        /// 
        /// **Response:**
        /// Returns the created order with calculated price, distance, ETA, and driver info.
        /// </remarks>
        /// <param name="request">Order acceptance request with pickup/destination, payment, preferences, and requested time</param>
        /// <response code="200">Order accepted successfully</response>
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

            // Determine if this is a scheduled ride
            bool isScheduled = request.RequestedTime.HasValue && request.RequestedTime.Value > DateTime.UtcNow.AddMinutes(5);

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId.Value,
                CreatedAt = DateTime.UtcNow,
                Status = isScheduled ? "scheduled" : "assigned",
                Action = "ride",
                PickupLat = request.FromLat,
                PickupLng = request.FromLng,
                Pickup = $"Lat: {request.FromLat}, Lng: {request.FromLng}",
                PaymentMethod = request.PaymentMethod.ToLower(),
                PetAllowed = request.Pet,
                ChildSeat = request.Child,
                Tariff = request.Tariff,
                VehicleType = null, // No vehicle type for taxi orders
                ScheduledFor = request.RequestedTime,
                // Pre-assign driver for both immediate and scheduled orders
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
                order.ScheduledFor,
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
                order.Tariff
            });
        }

        /// <summary>
        /// Request a new order (supports both taxi and delivery)
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

            // Determine if this is a delivery or taxi order based on Action field
            bool isDelivery = !string.IsNullOrEmpty(order.Action) && order.Action.ToLower() == "delivery";

            if (order.ScheduledFor.HasValue && order.ScheduledFor.Value > DateTime.UtcNow)
            {
                order.Status = "scheduled";
                var distance = HaversineDistanceKm(order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value);
                var eta = (int)Math.Ceiling(distance / 0.5);
                
                // For delivery, use vehicle type; for taxi, use tariff
                var price = CalculatePrice(distance, eta, order.PickupLat.Value, order.PickupLng.Value, 
                    order.DestLat.Value, order.DestLng.Value, 
                    isDelivery ? null : order.Tariff, 
                    isDelivery ? order.VehicleType : null, 
                    order.PetAllowed, order.ChildSeat);

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
            
            // For delivery, use vehicle type; for taxi, use tariff
            var priceNow = CalculatePrice(distanceNow, etaNow, order.PickupLat.Value, order.PickupLng.Value, 
                order.DestLat.Value, order.DestLng.Value, 
                isDelivery ? null : order.Tariff, 
                isDelivery ? order.VehicleType : null, 
                order.PetAllowed, order.ChildSeat);

            order.DistanceKm = Math.Round(distanceNow, 2);
            order.EtaMinutes = etaNow;
            order.Price = priceNow;

            _db.Orders.Add(order);
            await _db.SaveChangesAsync();

            // For delivery orders, use vehicle type in socket notifications
            var notificationType = isDelivery && !string.IsNullOrEmpty(order.VehicleType) 
                ? order.VehicleType.ToLower() 
                : "taxi";
            
            await _socketService.NotifyOrderEventAsync(order.Id, $"{notificationType}Finding", new { status = "searching" });

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

                await _socketService.NotifyOrderEventAsync(order.Id, $"{notificationType}Found", new { driver = new { id = driver.Id, name = driver.Name, phone = driver.Phone } });
            }

            return Ok(order);
        }

        /// <summary>
        /// Get ride estimate with order details (supports both taxi and delivery)
        /// </summary>
        [Authorize]
        [HttpPost("estimate/body")]
        public IActionResult Estimate([FromBody] Order order)
        {
            if (!order.PickupLat.HasValue || !order.DestLat.HasValue || !order.PickupLng.HasValue || !order.DestLng.HasValue)
                return BadRequest("Coordinates required for estimate (PickupLat, PickupLng, DestLat, DestLng).");

            var distance = HaversineDistanceKm(order.PickupLat.Value, order.PickupLng.Value, order.DestLat.Value, order.DestLng.Value);
            var eta = (int)Math.Ceiling(distance / 0.5);
            
            // Determine if this is a delivery or taxi order based on Action field
            bool isDelivery = !string.IsNullOrEmpty(order.Action) && order.Action.ToLower() == "delivery";
            
            // For delivery, use vehicle type; for taxi, use tariff
            var price = CalculatePrice(distance, eta, order.PickupLat.Value, order.PickupLng.Value, 
                order.DestLat.Value, order.DestLng.Value, 
                isDelivery ? null : order.Tariff, 
                isDelivery ? order.VehicleType : null, 
                order.PetAllowed, order.ChildSeat);

            return Ok(new { distanceKm = Math.Round(distance, 2), price, etaMinutes = eta });
        }

        /// <summary>
        /// Get ride estimate by coordinates (supports both taxi and delivery)
        /// </summary>
        /// <remarks>
        /// For taxi orders: Use 'tariff' parameter (electro, economy, comfort, business, premium)
        /// For delivery orders: Use 'vehicleType' parameter (moto, car, van)
        /// If both are provided, vehicleType takes precedence (delivery mode)
        /// </remarks>
        [Authorize]
        [HttpGet("estimate")]
        public IActionResult EstimateGet(
            [FromQuery] double pickupLat, 
            [FromQuery] double pickupLng, 
            [FromQuery] double destLat, 
            [FromQuery] double destLng, 
            [FromQuery] string? vehicleType, 
            [FromQuery] string? tariff, 
            [FromQuery] bool pet = false, 
            [FromQuery] bool child = false)
        {
            var distance = HaversineDistanceKm(pickupLat, pickupLng, destLat, destLng);
            var eta = (int)Math.Ceiling(distance / 0.5);
            
            // If vehicleType is provided, it's a delivery order; otherwise, it's a taxi order
            bool isDelivery = !string.IsNullOrEmpty(vehicleType);
            
            var price = CalculatePrice(distance, eta, pickupLat, pickupLng, destLat, destLng, 
                isDelivery ? null : tariff, 
                isDelivery ? vehicleType : null, 
                pet, child);
            
            var result = new 
            { 
                distanceKm = Math.Round(distance, 2), 
                price, 
                etaMinutes = eta
            };
            
            // Include the type that was used in the calculation
            if (isDelivery)
            {
                return Ok(new { result.distanceKm, result.price, result.etaMinutes, vehicleType });
            }
            else
            {
                return Ok(new { result.distanceKm, result.price, result.etaMinutes, tariff });
            }
        }

        /// <summary>
        /// Get all available tariffs with surcharges
        /// </summary>
        /// <remarks>
        /// Returns 5 tariff options with their surcharge amounts.
        /// No location parameters required.
        /// 
        /// **Tariff Tiers with Surcharges:**
        /// 1. **Standard (Electro)** - +100 AMD surcharge
        /// 2. **Start (Economy)** - +200 AMD surcharge
        /// 3. **Comfort** - +300 AMD surcharge
        /// 4. **Business** - +400 AMD surcharge
        /// 5. **Premium** - +500 AMD surcharge
        /// 
        /// **Example Request:**
        /// ```
        /// GET /api/orders/tariffs
        /// ```
        /// 
        /// **Example Response:**
        /// ```json
        /// [
        ///   {
        ///     "tariff": "electro",
        ///     "surcharge": 100
        ///   },
        ///   {
        ///     "tariff": "economy",
        ///     "surcharge": 200
        ///   },
        ///   ...
        /// ]
        /// ```
        /// </remarks>
        /// <response code="200">Returns list of 5 tariffs with surcharge amounts</response>
        [Authorize]
        [HttpGet("tariffs")]
        [ProducesResponseType(200)]
        public IActionResult GetTariffs()
        {
            var tariffs = new[]
            {
                new { tariff = "electro", surcharge = 100 },
                new { tariff = "economy", surcharge = 200 },
                new { tariff = "comfort", surcharge = 300 },
                new { tariff = "business", surcharge = 400 },
                new { tariff = "premium", surcharge = 500 }
            };

            return Ok(tariffs);
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

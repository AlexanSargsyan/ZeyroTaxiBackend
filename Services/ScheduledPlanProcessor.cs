using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System.Data.Common;
using Taxi_API.Data;
using Taxi_API.DTOs;
using Taxi_API.Models;

namespace Taxi_API.Services
{
    // Background service that periodically checks scheduled plans and creates Orders when occurrences are due.
    public class ScheduledPlanProcessor : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ScheduledPlanProcessor> _logger;
        private readonly ISocketService _socketService;

        public ScheduledPlanProcessor(IServiceScopeFactory scopeFactory, ILogger<ScheduledPlanProcessor> logger, ISocketService socketService)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _socketService = socketService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ScheduledPlanProcessor started");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessOnceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in ScheduledPlanProcessor");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            _logger.LogInformation("ScheduledPlanProcessor stopped");
        }

        private async Task ProcessOnceAsync(CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            // consider a small window, e.g., entries scheduled for the next 1 minute
            var windowStart = now;
            var windowEnd = now.AddMinutes(1);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var fcm = scope.ServiceProvider.GetService<IFcmService>();

            // Defensive: check if ScheduledPlans table exists
            try
            {
                var conn = db.Database.GetDbConnection();
                await conn.OpenAsync(ct);
                await using (conn)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='ScheduledPlans';";
                    var res = await cmd.ExecuteScalarAsync(ct);
                    if (res == null || res == DBNull.Value)
                    {
                        _logger.LogInformation("ScheduledPlanProcessor: ScheduledPlans table not found, skipping this run.");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ScheduledPlanProcessor: could not verify ScheduledPlans table existence, skipping this run.");
                return;
            }

            List<ScheduledPlan> plans;
            try
            {
                plans = await db.ScheduledPlans.ToListAsync(ct);
            }
            catch (SqliteException ex)
            {
                if (ex.Message?.Contains("no such table", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.LogInformation("ScheduledPlanProcessor: ScheduledPlans table not found (caught), skipping this run.");
                    return;
                }
                throw;
            }

            foreach (var plan in plans)
            {
                ScheduleEntry[] entries;
                try
                {
                    entries = JsonSerializer.Deserialize<ScheduleEntry[]>(plan.EntriesJson) ?? Array.Empty<ScheduleEntry>();
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < entries.Length; i++)
                {
                    var e = entries[i];
                    
                    // Parse time for this entry
                    if (!TimeSpan.TryParse(e.Time, out var timeOfDay)) continue;
                    
                    // Check if any of the days array matches today and the time is in the window
                    if (e.Days == null || e.Days.Length == 0) continue;

                    foreach (var day in e.Days)
                    {
                        var next = NextOccurrenceUtc(day, timeOfDay, now);
                        if (next < windowStart || next > windowEnd) continue;

                        // Check if already executed for this occurrence
                        var executionKey = $"{plan.Id}_{i}_{day}_{next.Date:yyyy-MM-dd}";
                        var already = await db.ScheduledPlanExecutions.AnyAsync(
                            x => x.PlanId == plan.Id && 
                                 x.EntryIndex == i &&
                                 x.OccurrenceDate.Date == next.Date,
                            ct);
                        
                        if (already) continue;

                        // Create order for this occurrence
                        var order = new Order
                        {
                            Id = Guid.NewGuid(),
                            UserId = plan.UserId,
                            CreatedAt = DateTime.UtcNow,
                            Action = "ride",
                            Pickup = e.PickupAddress,
                            Destination = e.DestinationAddress,
                            PickupLat = e.PickupLat,
                            PickupLng = e.PickupLng,
                            DestLat = e.DestinationLat,
                            DestLng = e.DestinationLng,
                            Status = "searching",
                            Tariff = e.Tariff,
                            PaymentMethod = e.PaymentMethod,
                            PetAllowed = e.Pet,
                            ChildSeat = e.Child
                        };

                        // Compute estimates
                        var distance = HaversineDistanceKm(e.PickupLat, e.PickupLng, e.DestinationLat, e.DestinationLng);
                        var eta = (int)Math.Ceiling(distance / 0.5);
                        var price = CalculateTaxiPrice(distance, eta, e.Tariff, e.Pet, e.Child);
                        
                        order.DistanceKm = Math.Round(distance, 2);
                        order.EtaMinutes = eta;
                        order.Price = price;

                        db.Orders.Add(order);
                        await db.SaveChangesAsync(ct);

                        // Mark executed
                        db.ScheduledPlanExecutions.Add(new ScheduledPlanExecution 
                        { 
                            PlanId = plan.Id,
                            EntryIndex = i,
                            OccurrenceDate = next,
                            ExecutedAt = DateTime.UtcNow
                        });
                        await db.SaveChangesAsync(ct);

                        // Notify via socket
                        await _socketService.NotifyOrderEventAsync(order.Id, "taxiFinding", new { status = "searching" });

                        // Send push notification to user if token available
                        try
                        {
                            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == plan.UserId, ct);
                            if (user != null && !string.IsNullOrWhiteSpace(user.PushToken) && fcm != null)
                            {
                                await fcm.SendPushAsync(
                                    user.PushToken, 
                                    "Scheduled ride created", 
                                    $"Your scheduled ride from {e.PickupAddress} to {e.DestinationAddress} has been created.", 
                                    new Dictionary<string, string> { { "orderId", order.Id.ToString() } }
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to send FCM for scheduled order");
                        }

                        // Try assign driver
                        var driver = await db.Users.FirstOrDefaultAsync(u => u.IsDriver && u.DriverProfile != null, ct);
                        if (driver != null)
                        {
                            order.DriverId = driver.Id;
                            order.DriverName = driver.Name;
                            order.DriverPhone = driver.Phone;
                            
                            var driverProfile = await db.DriverProfiles.FirstOrDefaultAsync(dp => dp.UserId == driver.Id, ct);
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
                            await db.SaveChangesAsync(ct);

                            await _socketService.NotifyOrderEventAsync(order.Id, "taxiFound", new 
                            { 
                                driver = new 
                                { 
                                    id = driver.Id, 
                                    name = driver.Name, 
                                    phone = driver.Phone, 
                                    car = order.DriverCar, 
                                    plate = order.DriverPlate 
                                } 
                            });

                            // Send push to user about driver assigned
                            try
                            {
                                var user2 = await db.Users.FirstOrDefaultAsync(u => u.Id == plan.UserId, ct);
                                if (user2 != null && !string.IsNullOrWhiteSpace(user2.PushToken) && fcm != null)
                                {
                                    await fcm.SendPushAsync(
                                        user2.PushToken, 
                                        "Driver assigned", 
                                        $"Driver {driver.Name} ({driver.Phone}) has been assigned to your scheduled ride.", 
                                        new Dictionary<string, string> { { "orderId", order.Id.ToString() } }
                                    );
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to send FCM for assigned driver");
                            }
                        }
                    }
                }
            }
        }

        private static DateTime NextOccurrenceUtc(DayOfWeek day, TimeSpan timeOfDay, DateTime now)
        {
            // Find next date (including today) which has given DayOfWeek
            var daysDiff = ((int)day - (int)now.DayOfWeek + 7) % 7;
            var candidateDate = now.Date.AddDays(daysDiff).Add(timeOfDay);
            if (candidateDate < now) candidateDate = candidateDate.AddDays(7);
            return DateTime.SpecifyKind(candidateDate, DateTimeKind.Utc);
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

        private static decimal CalculateTaxiPrice(double distanceKm, int etaMinutes, string? tariff, bool pet, bool child)
        {
            decimal baseFare = 400m;
            decimal perKm = 60m;
            decimal perMinute = 20m;

            // Apply tariff surcharges
            if (!string.IsNullOrEmpty(tariff))
            {
                switch (tariff.ToLower())
                {
                    case "electro":
                    case "standard":
                        baseFare += 100m;
                        break;
                    case "economy":
                    case "start":
                        baseFare += 200m;
                        break;
                    case "comfort":
                        baseFare += 300m;
                        break;
                    case "business":
                        baseFare += 400m;
                        break;
                    case "premium":
                        baseFare += 500m;
                        break;
                }
            }

            var price = baseFare + (decimal)distanceKm * perKm + (decimal)etaMinutes * perMinute;
            if (pet) price += 100m;
            if (child) price += 50m;

            // Minimum prices per tariff
            decimal minPrice = 800m;
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
}

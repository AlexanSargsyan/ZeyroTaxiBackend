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

            // Defensive: check if ScheduledPlans table exists (handles DB files created before this model was added)
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
                // If we couldn't check table existence, log and skip to avoid crashing the service
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
                // If the ScheduledPlans table does not exist yet (race or other), skip processing silently.
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
                    // build next occurrence for this entry in UTC based on DayOfWeek and Time string (HH:mm)
                    if (!TimeSpan.TryParse(e.Time, out var timeOfDay)) continue;

                    var next = NextOccurrenceUtc(e.Day, timeOfDay, now);
                    if (next < windowStart || next > windowEnd) continue;

                    // check if already executed for this occurrence
                    var already = await db.ScheduledPlanExecutions.AnyAsync(x => x.PlanId == plan.Id && x.EntryIndex == i && x.OccurrenceDate == next.Date, ct);
                    if (already) continue;

                    // create order for this occurrence
                    var order = new Order
                    {
                        Id = Guid.NewGuid(),
                        UserId = plan.UserId,
                        CreatedAt = DateTime.UtcNow,
                        Action = "taxi",
                        Pickup = e.Address,
                        Destination = e.Address,
                        PickupLat = e.Lat,
                        PickupLng = e.Lng,
                        DestLat = e.Lat,
                        DestLng = e.Lng,
                        Status = "searching"
                    };

                    // compute estimates
                    var distance = 0.0;
                    var eta = 1;
                    var price = 800m;
                    order.DistanceKm = distance;
                    order.EtaMinutes = eta;
                    order.Price = price;

                    db.Orders.Add(order);
                    await db.SaveChangesAsync(ct);

                    // mark executed
                    db.ScheduledPlanExecutions.Add(new ScheduledPlanExecution { PlanId = plan.Id, EntryIndex = i, OccurrenceDate = next.Date, ExecutedAt = DateTime.UtcNow });
                    await db.SaveChangesAsync(ct);

                    // notify via socket
                    await _socketService.NotifyOrderEventAsync(order.Id, "carFinding", new { status = "searching" });

                    // send push notification to user if token available
                    try
                    {
                        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == plan.UserId);
                        if (user != null && !string.IsNullOrWhiteSpace(user.PushToken) && fcm != null)
                        {
                            await fcm.SendPushAsync(user.PushToken, "Scheduled ride created", $"Your scheduled ride for {next:yyyy-MM-dd HH:mm} has been created.", new Dictionary<string, string> { { "orderId", order.Id.ToString() } });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send FCM for scheduled order");
                    }

                    // notify via socket
                    await _socketService.NotifyOrderEventAsync(order.Id, "carFinding", new { status = "searching" });

                    // try assign driver
                    var driver = await db.Users.FirstOrDefaultAsync(u => u.IsDriver && u.DriverProfile != null, ct);
                    if (driver != null)
                    {
                        order.DriverId = driver.Id;
                        order.DriverName = driver.Name;
                        order.DriverPhone = driver.Phone;
                        order.DriverCar = "Toyota";
                        order.DriverPlate = "510ZR10";
                        order.Status = "assigned";
                        order.EtaMinutes = 5;
                        await db.SaveChangesAsync(ct);

                        await _socketService.NotifyOrderEventAsync(order.Id, "carFound", new { driver = new { id = driver.Id, name = driver.Name, phone = driver.Phone } });

                        // send push to user about driver assigned
                        try
                        {
                            var user2 = await db.Users.FirstOrDefaultAsync(u => u.Id == plan.UserId);
                            if (user2 != null && !string.IsNullOrWhiteSpace(user2.PushToken) && fcm != null)
                            {
                                await fcm.SendPushAsync(user2.PushToken, "Driver assigned", $"Driver {driver.Name} ({driver.Phone}) has been assigned to your scheduled ride.", new Dictionary<string, string> { { "orderId", order.Id.ToString() } });
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to send FCM for assigned driver");
                        }
                    }
                }
            }

            // additionally: send reminder for completed orders or other scheduled notifications (example)
            try
            {
                // Defensive: ensure Orders table exists before querying
                try
                {
                    var conn3 = db.Database.GetDbConnection();
                    await conn3.OpenAsync(ct);
                    await using (conn3)
                    {
                        using var checkCmd = conn3.CreateCommand();
                        checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Orders';";
                        var exists = await checkCmd.ExecuteScalarAsync(ct);
                        if (exists == null || exists == DBNull.Value)
                        {
                            _logger.LogDebug("ScheduledPlanProcessor: Orders table not found, skipping completed orders notifications.");
                            goto SkipCompletedNotifications;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ScheduledPlanProcessor: Failed to verify Orders table existence, skipping completed orders notifications.");
                    goto SkipCompletedNotifications;
                }

                var recentlyCompleted = await db.Orders.Where(o => o.CompletedAt.HasValue && o.CompletedAt.Value > DateTime.UtcNow.AddMinutes(-5)).ToListAsync();
                foreach (var oc in recentlyCompleted)
                {
                    var usr = await db.Users.FirstOrDefaultAsync(u => u.Id == oc.UserId);
                    if (usr != null && !string.IsNullOrWhiteSpace(usr.PushToken) && fcm != null)
                    {
                        await fcm.SendPushAsync(usr.PushToken, "Ride completed", $"Your ride {oc.Id} was completed.", new Dictionary<string, string> { { "orderId", oc.Id.ToString() } });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send FCM for completed orders");
            }

        SkipCompletedNotifications: ;
        }

        private static DateTime NextOccurrenceUtc(DayOfWeek day, TimeSpan timeOfDay, DateTime now)
        {
            // find next date (including today) which has given DayOfWeek
            var daysDiff = ((int)day - (int)now.DayOfWeek + 7) % 7;
            var candidateDate = now.Date.AddDays(daysDiff).Add(timeOfDay);
            if (candidateDate < now) candidateDate = candidateDate.AddDays(7);
            return DateTime.SpecifyKind(candidateDate, DateTimeKind.Utc);
        }
    }
}

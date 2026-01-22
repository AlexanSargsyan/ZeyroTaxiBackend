namespace Taxi_API.DTOs
{
    /// <summary>
    /// Schedule entry for creating recurring orders
    /// </summary>
    /// <param name="Name">Optional name for this schedule entry</param>
    /// <param name="PickupAddress">Pickup location address</param>
    /// <param name="PickupLat">Pickup latitude coordinate</param>
    /// <param name="PickupLng">Pickup longitude coordinate</param>
    /// <param name="DestinationAddress">Destination address</param>
    /// <param name="DestinationLat">Destination latitude coordinate</param>
    /// <param name="DestinationLng">Destination longitude coordinate</param>
    /// <param name="Days">Array of days when this order should be scheduled (Monday, Tuesday, etc.)</param>
    /// <param name="Time">Time of day in 24-hour format (e.g., "08:30", "17:00")</param>
    /// <param name="Tariff">Tariff type for taxi orders (electro, economy, comfort, business, premium)</param>
    /// <param name="PaymentMethod">Payment method - "cash" or "card"</param>
    /// <param name="Pet">Pet allowed (+100 AMD surcharge)</param>
    /// <param name="Child">Child seat required (+50 AMD surcharge)</param>
    public record ScheduleEntry(
        string? Name,
        string PickupAddress, 
        double PickupLat, 
        double PickupLng, 
        string DestinationAddress,
        double DestinationLat, 
        double DestinationLng, 
        DayOfWeek[] Days,
        string Time,
        string? Tariff = "economy",
        string PaymentMethod = "cash",
        bool Pet = false,
        bool Child = false
    );

    /// <summary>
    /// Request to create a new scheduled plan
    /// </summary>
    /// <param name="Name">Optional name for the entire plan (e.g., "Work Week Commute")</param>
    /// <param name="Entries">Array of schedule entries (different routes/times)</param>
    public record CreateScheduleRequest(string? Name, ScheduleEntry[] Entries);

    /// <summary>
    /// Response containing scheduled plan details
    /// </summary>
    public record ScheduledPlanResponse(Guid Id, string? Name, ScheduleEntry[] Entries, DateTime CreatedAt);
}
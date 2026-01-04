using System.ComponentModel.DataAnnotations;

namespace Taxi_API.Models
{
    public class Order
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        // action: taxi, delivery, schedule
        public string Action { get; set; } = null!;

        public Guid UserId { get; set; }
        public User? User { get; set; }

        public string? Pickup { get; set; }
        public string? Destination { get; set; }

        // Optional coordinates to calculate estimates
        public double? PickupLat { get; set; }
        public double? PickupLng { get; set; }
        public double? DestLat { get; set; }
        public double? DestLng { get; set; }

        // For delivery or scheduled orders
        public string? PackageDetails { get; set; }
        public DateTime? ScheduledFor { get; set; }

        public string Status { get; set; } = "searching"; // searching, assigned, on_trip, completed, cancelled

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public DateTime? CancelledAt { get; set; }
        public string? CancelReason { get; set; }

        // Driver assignment
        public Guid? DriverId { get; set; }
        public User? Driver { get; set; }
        public string? DriverName { get; set; }
        public string? DriverPhone { get; set; }
        public string? DriverCar { get; set; }
        public string? DriverPlate { get; set; }

        // Estimates
        public int? EtaMinutes { get; set; }
        public double? DistanceKm { get; set; }
        public decimal? Price { get; set; }

        // Payment
        public string? PaymentMethod { get; set; }

        // Rating
        public int? Rating { get; set; }
        public string? Review { get; set; }
    }
}

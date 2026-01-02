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

        // For delivery or scheduled orders
        public string? PackageDetails { get; set; }
        public DateTime? ScheduledFor { get; set; }

        public string Status { get; set; } = "pending";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}

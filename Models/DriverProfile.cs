using System.ComponentModel.DataAnnotations;

namespace Taxi_API.Models
{
    public class DriverProfile
    {
        public int Id { get; set; }

        public Guid UserId { get; set; }

        public User? User { get; set; }

        public bool Approved { get; set; }

        public List<Photo> Photos { get; set; } = new();

        public List<Order> Orders { get; set; } = new();

        public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

        // results of automated checks
        public bool? FaceMatch { get; set; }
        public bool? CarOk { get; set; }
    }
}
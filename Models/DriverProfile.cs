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

        // Car information
        public string? CarMake { get; set; }
        public string? CarModel { get; set; }
        public string? CarColor { get; set; }
        public string? CarPlate { get; set; }
        public int? CarYear { get; set; }

        // Stripe Connect account id for payouts
        public string? StripeAccountId { get; set; }

        // Passport details
        public string? PassportNumber { get; set; }
        public string? PassportName { get; set; }
        public DateTime? PassportExpiry { get; set; }
        public string? PassportCountry { get; set; }

        // Driving license details
        public string? LicenseNumber { get; set; }
        public string? LicenseName { get; set; }
        public DateTime? LicenseExpiry { get; set; }
        public string? LicenseIssuingCountry { get; set; }

        // Current location (for driver tracking)
        public double? CurrentLat { get; set; }
        public double? CurrentLng { get; set; }
        public DateTime? LastLocationAt { get; set; }
    }
}
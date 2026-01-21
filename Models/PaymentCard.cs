using System.ComponentModel.DataAnnotations;

namespace Taxi_API.Models
{
    public class PaymentCard
    {
        [Key]
        public int Id { get; set; }

        public Guid UserId { get; set; }

        public string? TokenHash { get; set; }

        public string Last4 { get; set; } = null!;

        public string? Brand { get; set; }

        public string? CardholderName { get; set; }

        public int ExpMonth { get; set; }
        public int ExpYear { get; set; }

        public bool IsDefault { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
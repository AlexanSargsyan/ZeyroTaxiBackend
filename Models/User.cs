using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Taxi_API.Models
{
    public class User
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public string Phone { get; set; } = null!;

        public string? Name { get; set; }

        public bool IsDriver { get; set; }

        public DriverProfile? DriverProfile { get; set; }

        public List<Order> Orders { get; set; } = new();

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
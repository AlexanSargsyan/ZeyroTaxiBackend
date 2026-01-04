using Microsoft.EntityFrameworkCore;
using Taxi_API.Models;

namespace Taxi_API.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users => Set<User>();
        public DbSet<DriverProfile> DriverProfiles => Set<DriverProfile>();
        public DbSet<Photo> Photos => Set<Photo>();
        public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>()
                .HasIndex(u => u.Phone)
                .IsUnique();

            modelBuilder.Entity<User>()
                .HasOne(u => u.DriverProfile)
                .WithOne(dp => dp.User)
                .HasForeignKey<DriverProfile>(dp => dp.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<DriverProfile>()
                .HasMany(dp => dp.Photos)
                .WithOne(p => p.DriverProfile)
                .HasForeignKey(p => p.DriverProfileId)
                .OnDelete(DeleteBehavior.Cascade);

            // Basic configuration for Order and explicit relationships to User
            modelBuilder.Entity<Order>()
                .HasKey(o => o.Id);

            modelBuilder.Entity<Order>()
                .HasOne(o => o.User)
                .WithMany(u => u.Orders)
                .HasForeignKey(o => o.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Order>()
                .HasOne(o => o.Driver)
                .WithMany() // no navigation property on User for driven orders
                .HasForeignKey(o => o.DriverId)
                .OnDelete(DeleteBehavior.Restrict);

            base.OnModelCreating(modelBuilder);
        }
    }
}
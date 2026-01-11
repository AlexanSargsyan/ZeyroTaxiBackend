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
        public DbSet<PaymentCard> PaymentCards => Set<PaymentCard>();
        public DbSet<ScheduledPlan> ScheduledPlans => Set<ScheduledPlan>();
        public DbSet<ScheduledPlanExecution> ScheduledPlanExecutions => Set<ScheduledPlanExecution>();

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

            modelBuilder.Entity<PaymentCard>()
                .HasKey(pc => pc.Id);
            modelBuilder.Entity<PaymentCard>().HasIndex(pc => new { pc.UserId, pc.Last4 });

            modelBuilder.Entity<ScheduledPlan>()
                .HasKey(s => s.Id);

            modelBuilder.Entity<ScheduledPlan>()
                .HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ScheduledPlanExecution>()
                .HasKey(e => e.Id);

            modelBuilder.Entity<ScheduledPlanExecution>()
                .HasOne(e => e.Plan)
                .WithMany()
                .HasForeignKey(e => e.PlanId)
                .OnDelete(DeleteBehavior.Cascade);

            base.OnModelCreating(modelBuilder);
        }
    }
}
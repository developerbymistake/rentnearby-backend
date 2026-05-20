using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;

namespace RentNearBy.Infrastructure.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<User> Users { get; set; }
    public DbSet<Session> Sessions { get; set; }
    public DbSet<Listing> Listings { get; set; }
    public DbSet<ListingPhoto> ListingPhotos { get; set; }
    public DbSet<District> Districts { get; set; }
    public DbSet<City> Cities { get; set; }
    public DbSet<RoomType> RoomTypes { get; set; }
    public DbSet<Plan> Plans { get; set; }
    public DbSet<UserMembership> UserMemberships { get; set; }
    public DbSet<PaymentTransaction> PaymentTransactions { get; set; }
    public DbSet<PaymentFeature> PaymentFeatures { get; set; }
    public DbSet<Plot> Plots { get; set; }
    public DbSet<PlotPhoto> PlotPhotos { get; set; }
    public DbSet<PlotPlan> PlotPlans { get; set; }
    public DbSet<PlotMembership> PlotMemberships { get; set; }
    public DbSet<PlotPaymentFeature> PlotPaymentFeatures { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(u => u.PhoneNumber).IsUnique();
            e.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
            e.Property(u => u.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(s => s.UserId);
            e.HasIndex(s => s.ExpiresAt);
            e.HasOne(s => s.User)
             .WithMany()
             .HasForeignKey(s => s.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<District>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(d => d.Name).IsUnique();
            e.Property(d => d.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<City>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(c => c.DistrictId);
            e.HasIndex(c => new { c.DistrictId, c.Name }).IsUnique();
            e.HasOne(c => c.District)
             .WithMany(d => d.Cities)
             .HasForeignKey(c => c.DistrictId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoomType>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(r => r.Name).IsUnique();
            e.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
            e.HasData(
                new RoomType { Id = Guid.Parse("a1000000-0000-0000-0000-000000000001"), Name = "1BHK",   SortOrder = 1, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new RoomType { Id = Guid.Parse("a1000000-0000-0000-0000-000000000002"), Name = "2BHK",   SortOrder = 2, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new RoomType { Id = Guid.Parse("a1000000-0000-0000-0000-000000000003"), Name = "3BHK",   SortOrder = 3, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new RoomType { Id = Guid.Parse("a1000000-0000-0000-0000-000000000007"), Name = "1RK",    SortOrder = 4, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new RoomType { Id = Guid.Parse("a1000000-0000-0000-0000-000000000005"), Name = "PG",     SortOrder = 5, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new RoomType { Id = Guid.Parse("a1000000-0000-0000-0000-000000000004"), Name = "Hostel", SortOrder = 6, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
            );
        });

        modelBuilder.Entity<Plan>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(p => p.PlanType).IsUnique();
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            e.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Listing>(e =>
        {
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(l => l.CreatedAt).HasDefaultValueSql("now()");
            e.Property(l => l.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne(l => l.User)
             .WithMany(u => u.Listings)
             .HasForeignKey(l => l.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(l => l.RoomType)
             .WithMany(r => r.Listings)
             .HasForeignKey(l => l.RoomTypeId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(l => l.District)
             .WithMany(d => d.Listings)
             .HasForeignKey(l => l.DistrictId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(l => l.City)
             .WithMany(c => c.Listings)
             .HasForeignKey(l => l.CityId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);

            // FK indexes (explicit — cascade + join performance)
            e.HasIndex(l => l.DistrictId);
            e.HasIndex(l => l.CityId);
            e.HasIndex(l => l.UserId);
            e.HasIndex(l => l.RoomTypeId);
            // Filter indexes
            e.HasIndex(l => l.IsActive);
            e.HasIndex(l => l.PriceMonthly);
            e.HasIndex(l => l.CreatedAt);
            // Stored geography column — auto-computed from Latitude/Longitude by PostgreSQL
            e.Property<NetTopologySuite.Geometries.Point?>("Location")
             .HasColumnType("geography(Point, 4326)")
             .HasComputedColumnSql(
                 "ST_SetSRID(ST_MakePoint(\"Longitude\"::float8, \"Latitude\"::float8), 4326)::geography",
                 stored: true);
            e.HasIndex("Location")
             .HasMethod("gist")
             .HasDatabaseName("ix_listings_location_gist");
            // Composite: My Listings pagination — filter by user, sort newest first
            e.HasIndex(l => new { l.UserId, l.CreatedAt });
            // Composite: GetNearby secondary filter — city + active (GiST is primary spatial filter)
            e.HasIndex(l => new { l.CityId, l.IsActive });
            // Composite: city-based active listings newest first (replaces district composite)
            e.HasIndex(l => new { l.CityId, l.IsActive, l.CreatedAt });
            // Composite: search with room type filter
            e.HasIndex(l => new { l.IsActive, l.RoomTypeId });
        });

        modelBuilder.Entity<ListingPhoto>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(p => p.UploadedAt).HasDefaultValueSql("now()");
            e.HasOne(p => p.Listing)
             .WithMany(l => l.Photos)
             .HasForeignKey(p => p.ListingId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => p.ListingId);
        });

        modelBuilder.Entity<UserMembership>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(m => m.CreatedAt).HasDefaultValueSql("now()");
            e.Property(m => m.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne(m => m.User)
             .WithMany()
             .HasForeignKey(m => m.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => m.UserId);
            e.HasIndex(m => m.ValidUntil);
            e.HasIndex(m => m.IsActive);
        });

        modelBuilder.Entity<PaymentTransaction>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(t => t.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne(t => t.User)
             .WithMany()
             .HasForeignKey(t => t.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(t => t.Listing)
             .WithMany()
             .HasForeignKey(t => t.ListingId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(t => t.Plot)
             .WithMany()
             .HasForeignKey(t => t.PlotId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(t => t.UserId);
            e.HasIndex(t => t.Status);
            e.HasIndex(t => t.CreatedAt);
            e.HasIndex(t => t.RazorpayOrderId);
        });

        modelBuilder.Entity<PaymentFeature>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(f => f.CreatedAt).HasDefaultValueSql("now()");
            e.Property(f => f.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Plot>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            e.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne(p => p.User)
             .WithMany()
             .HasForeignKey(p => p.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(p => p.District)
             .WithMany()
             .HasForeignKey(p => p.DistrictId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(p => p.City)
             .WithMany()
             .HasForeignKey(p => p.CityId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(p => p.UserId);
            e.HasIndex(p => p.DistrictId);
            e.HasIndex(p => p.CityId);
            e.HasIndex(p => p.IsActive);
            e.HasIndex(p => p.CreatedAt);
            e.HasIndex(p => p.AreaSqft);

            e.Property<NetTopologySuite.Geometries.Point?>("Location")
             .HasColumnType("geography(Point, 4326)")
             .HasComputedColumnSql(
                 "ST_SetSRID(ST_MakePoint(\"Longitude\"::float8, \"Latitude\"::float8), 4326)::geography",
                 stored: true);
            e.HasIndex("Location")
             .HasMethod("gist")
             .HasDatabaseName("ix_plots_location_gist");

            e.HasIndex(p => new { p.UserId, p.CreatedAt });
            e.HasIndex(p => new { p.CityId, p.IsActive });
            e.HasIndex(p => new { p.CityId, p.IsActive, p.CreatedAt });
        });

        modelBuilder.Entity<PlotPhoto>(e =>
        {
            e.HasKey(ph => ph.Id);
            e.Property(ph => ph.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(ph => ph.UploadedAt).HasDefaultValueSql("now()");
            e.HasOne(ph => ph.Plot)
             .WithMany(p => p.Photos)
             .HasForeignKey(ph => ph.PlotId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(ph => ph.PlotId);
        });

        modelBuilder.Entity<PlotPlan>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(p => p.PlanType).IsUnique();
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            e.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<PlotMembership>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(m => m.CreatedAt).HasDefaultValueSql("now()");
            e.Property(m => m.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne(m => m.User)
             .WithMany()
             .HasForeignKey(m => m.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => m.UserId);
            e.HasIndex(m => m.IsActive);
            e.HasIndex(m => m.ValidUntil);
        });

        modelBuilder.Entity<PlotPaymentFeature>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(f => f.CreatedAt).HasDefaultValueSql("now()");
            e.Property(f => f.UpdatedAt).HasDefaultValueSql("now()");
        });
    }
}

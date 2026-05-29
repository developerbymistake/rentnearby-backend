using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;

namespace RentNearBy.Infrastructure.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<User> Users { get; set; }
    public DbSet<Session> Sessions { get; set; }
    public DbSet<Admin> Admins { get; set; }
    public DbSet<AdminSession> AdminSessions { get; set; }
    public DbSet<RoomListing> RoomListings { get; set; }
    public DbSet<RoomPhoto> RoomPhotos { get; set; }
    public DbSet<District> Districts { get; set; }
    public DbSet<City> Cities { get; set; }
    public DbSet<RoomType> RoomTypes { get; set; }
    public DbSet<RoomPlan> RoomPlans { get; set; }
    public DbSet<RoomMembership> RoomMemberships { get; set; }
    public DbSet<PaymentTransaction> PaymentTransactions { get; set; }
    public DbSet<AppFeature> AppFeatures { get; set; }
    public DbSet<PlotType> PlotTypes { get; set; }
    public DbSet<PlotListing> PlotListings { get; set; }
    public DbSet<PlotPhoto> PlotPhotos { get; set; }
    public DbSet<PlotPlan> PlotPlans { get; set; }
    public DbSet<PlotMembership> PlotMemberships { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Admin>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(a => a.PhoneNumber).IsUnique();
            e.Property(a => a.CreatedAt).HasDefaultValueSql("now()");
            e.Property(a => a.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<AdminSession>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne(s => s.Admin)
             .WithMany(a => a.Sessions)
             .HasForeignKey(s => s.AdminId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.AdminId);
            e.HasIndex(s => s.ExpiresAt);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(u => u.GoogleId).IsUnique();
            e.HasIndex(u => u.GoogleEmail).IsUnique();
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
            e.HasIndex(d => new { d.StateName, d.Name }).IsUnique(); // district name unique per state (same name can exist in different states)
            e.Property(d => d.CreatedAt).HasDefaultValueSql("now()");
            e.Property(d => d.StateName).IsRequired();
            e.Property(d => d.IsActive).HasDefaultValue(false);
            e.HasIndex(d => d.IsActive);
            e.HasIndex(d => d.StateName);
            e.Property(d => d.Boundary).HasColumnType("geometry(Geometry, 4326)");
            // Partial spatial index — only active districts in the GiST tree.
            // GetContext does: WHERE IsActive = true AND ST_Contains(Boundary, point)
            // Smaller index (active-only) means faster R-tree traversal than indexing all 700.
            e.HasIndex(d => d.Boundary)
             .HasMethod("gist")
             .HasDatabaseName("ix_districts_boundary_active_gist")
             .HasFilter("\"IsActive\" = true");
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

        modelBuilder.Entity<PlotType>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(p => p.Name).IsUnique();
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            e.HasData(
                new PlotType { Id = Guid.Parse("b1000000-0000-0000-0000-000000000001"), Name = "Residential",  SortOrder = 1, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new PlotType { Id = Guid.Parse("b1000000-0000-0000-0000-000000000002"), Name = "Commercial",   SortOrder = 2, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new PlotType { Id = Guid.Parse("b1000000-0000-0000-0000-000000000003"), Name = "Agricultural", SortOrder = 3, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
            );
        });

        modelBuilder.Entity<RoomPlan>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(p => p.PlanType).IsUnique();
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            e.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<RoomListing>(e =>
        {
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(l => l.CreatedAt).HasDefaultValueSql("now()");
            e.Property(l => l.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne(l => l.User)
             .WithMany()
             .HasForeignKey(l => l.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(l => l.RoomType)
             .WithMany()
             .HasForeignKey(l => l.RoomTypeId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(l => l.District)
             .WithMany()
             .HasForeignKey(l => l.DistrictId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(l => l.City)
             .WithMany()
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
            // Composite: DeleteDistrict guard — AnyAsync(l.DistrictId == id && l.IsActive)
            e.HasIndex(l => new { l.DistrictId, l.IsActive });
            // Stored geography column — auto-computed from Latitude/Longitude by PostgreSQL
            e.Property<NetTopologySuite.Geometries.Point?>("Location")
             .HasColumnType("geography(Point, 4326)")
             .HasComputedColumnSql(
                 "ST_SetSRID(ST_MakePoint(\"Longitude\"::float8, \"Latitude\"::float8), 4326)::geography",
                 stored: true);
            e.HasIndex("Location")
             .HasMethod("gist")
             .HasDatabaseName("ix_listings_location_gist");
            // Composite: My RoomListings pagination — filter by user, sort newest first
            e.HasIndex(l => new { l.UserId, l.CreatedAt });
            // Composite: admin count queries — COUNT(UserId = x AND !IsDeleted)
            e.HasIndex(l => new { l.UserId, l.IsDeleted });
            // Composite: GetNearby secondary filter — city + active (GiST is primary spatial filter)
            e.HasIndex(l => new { l.CityId, l.IsActive });
            // Composite: city-based active listings newest first (replaces district composite)
            e.HasIndex(l => new { l.CityId, l.IsActive, l.CreatedAt });
            // Composite: search with room type filter
            e.HasIndex(l => new { l.IsActive, l.RoomTypeId });
        });

        modelBuilder.Entity<RoomPhoto>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(p => p.UploadedAt).HasDefaultValueSql("now()");
            e.HasOne(p => p.RoomListing)
             .WithMany(l => l.Photos)
             .HasForeignKey(p => p.RoomListingId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => p.RoomListingId);
        });

        modelBuilder.Entity<RoomMembership>(e =>
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
            // Composite: get active membership for user (called on every listing activation)
            e.HasIndex(m => new { m.UserId, m.IsActive });
            // Composite: expiry background job — find expired active memberships
            e.HasIndex(m => new { m.IsActive, m.ValidUntil });
        });

        modelBuilder.Entity<PaymentTransaction>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(t => t.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne(t => t.User)
             .WithMany()
             .HasForeignKey(t => t.UserId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(t => t.RoomListing)
             .WithMany()
             .HasForeignKey(t => t.RoomListingId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(t => t.PlotListing)
             .WithMany()
             .HasForeignKey(t => t.PlotId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(t => t.UserId);
            e.HasIndex(t => t.Status);
            e.HasIndex(t => t.CreatedAt);
            e.HasIndex(t => t.RazorpayOrderId);
        });

        modelBuilder.Entity<AppFeature>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.Id).HasDefaultValueSql("gen_random_uuid()");
            e.HasIndex(f => f.Key).IsUnique();
            e.Property(f => f.CreatedAt).HasDefaultValueSql("now()");
            e.Property(f => f.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<PlotListing>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            e.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne(p => p.PlotType)
             .WithMany(t => t.PlotListings)
             .HasForeignKey(p => p.PlotTypeId)
             .OnDelete(DeleteBehavior.Restrict);

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

            e.HasIndex(p => p.PlotTypeId);
            e.HasIndex(p => p.UserId);
            e.HasIndex(p => p.DistrictId);
            e.HasIndex(p => p.CityId);
            e.HasIndex(p => p.IsActive);
            e.HasIndex(p => p.CreatedAt);
            e.HasIndex(p => p.AreaSqft);
            // Composite: DeleteDistrict guard — AnyAsync(p.DistrictId == id && p.IsActive)
            e.HasIndex(p => new { p.DistrictId, p.IsActive });

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
            // Composite: admin count queries — COUNT(UserId = x AND !IsDeleted)
            e.HasIndex(p => new { p.UserId, p.IsDeleted });
        });

        modelBuilder.Entity<PlotPhoto>(e =>
        {
            e.HasKey(ph => ph.Id);
            e.Property(ph => ph.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(ph => ph.UploadedAt).HasDefaultValueSql("now()");
            e.HasOne(ph => ph.PlotListing)
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
             .WithMany(u => u.PlotMemberships)
             .HasForeignKey(m => m.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => m.UserId);
            e.HasIndex(m => m.IsActive);
            e.HasIndex(m => m.ValidUntil);
            // Composite: get active membership for user (called on every plot activation)
            e.HasIndex(m => new { m.UserId, m.IsActive });
            // Composite: expiry background job
            e.HasIndex(m => new { m.IsActive, m.ValidUntil });
        });

    }
}

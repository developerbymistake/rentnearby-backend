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
            e.HasIndex(s => new { s.UserId, s.IsRevoked });
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
            // Geo index created as GiST via raw SQL in Program.cs startup
            // Composite: most common query — active listings in a district, newest first
            e.HasIndex(l => new { l.DistrictId, l.IsActive, l.CreatedAt });
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
    }
}

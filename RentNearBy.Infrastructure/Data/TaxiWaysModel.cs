using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Data;

// SavedJourneys, SavedJourneyLegs, TaxiRouteGeometries. No DbSet properties: repositories use Set<T>().
public static class TaxiWaysModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SavedJourney>(e =>
        {
            e.ToTable("SavedJourneys", t =>
            {
                t.HasCheckConstraint("ck_savedjourneys_people", "\"People\" BETWEEN 1 AND 12");
                t.HasCheckConstraint("ck_savedjourneys_cars", "\"Cars\" BETWEEN 1 AND 3");
                t.HasCheckConstraint("ck_savedjourneys_status", "\"Status\" IN " + JourneyStatuses.SqlList);
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property<uint>("xmin").IsRowVersion();
            e.Property(x => x.FromName).HasMaxLength(JourneyRules.NameMax).IsRequired();
            e.Property(x => x.ToName).HasMaxLength(JourneyRules.NameMax).IsRequired();
            e.Property(x => x.FromLatitude).HasColumnType("numeric(9,6)");
            e.Property(x => x.FromLongitude).HasColumnType("numeric(9,6)");
            e.Property(x => x.ToLatitude).HasColumnType("numeric(9,6)");
            e.Property(x => x.ToLongitude).HasColumnType("numeric(9,6)");
            e.Property(x => x.TravelDate).HasColumnType("date");
            e.Property(x => x.PlanKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.IsDeleted).HasDefaultValue(false);

            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Trip>().WithMany().HasForeignKey(x => x.TripId).IsRequired(false).OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.UserId, x.UpdatedAt }, "ix_savedjourneys_user")
             .IsDescending(false, true)
             .HasFilter("\"IsDeleted\" = false");
            e.HasIndex(x => x.TravelDate, "ix_savedjourneys_travel")
             .HasFilter("\"IsDeleted\" = false AND \"Status\" <> 'Completed'");
        });

        modelBuilder.Entity<SavedJourneyLeg>(e =>
        {
            e.ToTable("SavedJourneyLegs", t =>
            {
                t.HasCheckConstraint("ck_savedjourneylegs_status", "\"LastKnownStatus\" IN " + JourneyLegStatuses.SqlList);
                t.HasCheckConstraint("ck_savedjourneylegs_index", "\"LegIndex\" BETWEEN 1 AND 3");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.LegKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.FromName).HasMaxLength(JourneyRules.NameMax).IsRequired();
            e.Property(x => x.ToName).HasMaxLength(JourneyRules.NameMax).IsRequired();
            e.Property(x => x.MeetingPlace).HasMaxLength(JourneyRules.NameMax);
            e.Property(x => x.FromLatitude).HasColumnType("numeric(9,6)");
            e.Property(x => x.FromLongitude).HasColumnType("numeric(9,6)");
            e.Property(x => x.ToLatitude).HasColumnType("numeric(9,6)");
            e.Property(x => x.ToLongitude).HasColumnType("numeric(9,6)");
            e.Property(x => x.Partial).HasDefaultValue(false);
            e.Property(x => x.CoverTaxiIds).HasColumnType("uuid[]").HasDefaultValueSql("'{}'::uuid[]").IsRequired();
            e.Property(x => x.LastKnownStatus).HasMaxLength(12).HasDefaultValue(JourneyLegStatuses.Unknown).IsRequired();
            e.Property(x => x.DriverAvailable).HasDefaultValue(true);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne(x => x.Journey).WithMany(j => j.Legs).HasForeignKey(x => x.JourneyId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<TripEnquiry>().WithMany().HasForeignKey(x => x.LastEnquiryId).IsRequired(false).OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.JourneyId, x.LegIndex }, "ix_savedjourneylegs_journey_index").IsUnique();
            e.HasIndex(x => x.DriverTaxiId, "ix_savedjourneylegs_driver").HasFilter("\"DriverAvailable\" = true");
        });

        modelBuilder.Entity<TaxiRouteGeometry>(e =>
        {
            e.ToTable("TaxiRouteGeometries", t =>
                t.HasCheckConstraint("ck_taxiroutegeometries_status", "\"Status\" IN " + TaxiRouteGeometryStatuses.SqlList));
            e.HasKey(x => x.RouteId);
            e.Property(x => x.EndsHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasMaxLength(12).IsRequired();
            e.Property(x => x.GeometryJson).HasColumnType("jsonb");
            e.Property(x => x.DistanceKm).HasColumnType("numeric(7,1)");
            e.Property(x => x.Attempts).HasDefaultValue((short)0);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne<TaxiRoute>().WithOne().HasForeignKey<TaxiRouteGeometry>(x => x.RouteId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.Status, x.UpdatedAt }, "ix_taxiroutegeometries_status");
        });
    }
}

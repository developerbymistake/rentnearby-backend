using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Infrastructure.Data;

// Taxi request tables. No DbSet properties: code uses Set<T>(). Transitions are conditional updates, so no xmin.
// Double booking is guarded by the driver row lock plus an overlap check (no exclusion constraint, no btree_gist).
public static class TaxiRequestModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaxiRequest>(e =>
        {
            e.ToTable("TaxiRequests", t =>
            {
                t.HasCheckConstraint("ck_taxirequests_status", $"\"Status\" IN ({RequestStates.SqlList})");
                t.HasCheckConstraint("ck_taxirequests_sections", "\"WantPickup\" OR \"WantSightseeing\" OR \"WantDrop\"");
                t.HasCheckConstraint("ck_taxirequests_seats", $"\"Seats\" IN ({string.Join(", ", TaxiRequestSeats.Allowed)})");
                t.HasCheckConstraint("ck_taxirequests_dates", "\"DateTo\" >= \"DateFrom\"");
                t.HasCheckConstraint("ck_taxirequests_accepts",
                    $"\"AcceptedCount\" >= 0 AND \"AcceptedCount\" <= \"AcceptTarget\" AND \"AcceptTarget\" <= {TaxiDispatchRules.MaxAccepts}");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.EndReason).HasMaxLength(32);
            e.Property(x => x.CancelChip).HasMaxLength(20);
            e.Property(x => x.AdminNote).HasMaxLength(300);
            e.Property(x => x.PickupCityName).HasMaxLength(120);
            e.Property(x => x.DropCityName).HasMaxLength(120);
            e.Property(x => x.DateFrom).HasColumnType("date");
            e.Property(x => x.DateTo).HasColumnType("date");
            e.Property(x => x.SightseeingDaysJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.TripSnapshotJson).HasColumnType("jsonb").IsRequired();
            // Sentinel = the entity default, so an explicit other value is still sent on insert.
            e.Property(x => x.AcceptTarget).HasDefaultValue((short)TaxiDispatchRules.DefaultAcceptTarget)
             .HasSentinel((short)TaxiDispatchRules.DefaultAcceptTarget);
            e.Property(x => x.AcceptedCount).HasDefaultValue((short)0);
            e.Property(x => x.Version).HasDefaultValue(1L).HasSentinel(1L);
            e.Property(x => x.WaitRound).HasDefaultValue(0);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne<Trip>().WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.TravellerUserId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.TripId, "ux_taxirequests_active_trip")
             .IsUnique()
             .HasFilter($"\"Status\" IN ({RequestStates.ActiveSqlList})");
            e.HasIndex(x => new { x.TravellerUserId, x.ClientRequestId }, "ux_taxirequests_client")
             .IsUnique()
             .HasFilter("\"ClientRequestId\" IS NOT NULL");
            e.HasIndex(x => new { x.DispatchCityKey, x.CreatedAt, x.Id }, "ix_taxirequests_dispatch")
             .HasFilter($"\"Status\" IN ({TaxiSqlLists.Dispatching})");
            e.HasIndex(x => new { x.TravellerUserId, x.CreatedAt }, "ix_taxirequests_traveller")
             .IsDescending(false, true);
            e.HasIndex(x => x.ClosedAt, "ix_taxirequests_closed")
             .HasFilter("\"ClosedAt\" IS NOT NULL");
        });

        modelBuilder.Entity<TaxiRequestOffer>(e =>
        {
            e.ToTable("TaxiRequestOffers", t =>
                t.HasCheckConstraint("ck_taxioffers_status", $"\"Status\" IN ({OfferStates.SqlList})"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.EndReason).HasMaxLength(32);
            e.Property(x => x.Chip).HasMaxLength(20);
            e.Property(x => x.OfferedAt).HasDefaultValueSql("now()");
            e.Property(x => x.PushLate).HasDefaultValue(false);

            e.HasOne<TaxiRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.RequestId, x.DriverListingId }, "ux_taxioffers_request_driver").IsUnique();
            // One ring at a time per driver, in the database.
            e.HasIndex(x => x.DriverListingId, "ux_taxioffers_driver_ringing")
             .IsUnique()
             .HasFilter($"\"Status\" = '{OfferStates.Ringing}'");
            e.HasIndex(x => new { x.DriverListingId, x.OfferedAt }, "ix_taxioffers_driver_list")
             .IsDescending(false, true);
            e.HasIndex(x => new { x.RequestId, x.Status }, "ix_taxioffers_request");
            e.HasIndex(x => x.DriverListingId, "ix_taxioffers_driver_selected")
             .HasFilter($"\"Status\" = '{OfferStates.Selected}'");
        });

        modelBuilder.Entity<TaxiBooking>(e =>
        {
            e.ToTable("TaxiBookings", t =>
            {
                t.HasCheckConstraint("ck_taxibookings_status", $"\"Status\" IN ({BookingStates.SqlList})");
                t.HasCheckConstraint("ck_taxibookings_dates", "\"DateTo\" >= \"DateFrom\"");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.DateFrom).HasColumnType("date");
            e.Property(x => x.DateTo).HasColumnType("date");
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.CancelledBy).HasMaxLength(12);
            e.Property(x => x.CancelChip).HasMaxLength(20);
            e.Property(x => x.CompletedBy).HasMaxLength(12);
            e.Property(x => x.TripName).HasMaxLength(80).IsRequired();
            e.Property(x => x.PickupCityName).HasMaxLength(120).IsRequired();
            e.Property(x => x.DriverName).HasMaxLength(120).IsRequired();
            e.Property(x => x.VehicleName).HasMaxLength(120).IsRequired();
            e.Property(x => x.TravellerName).HasMaxLength(120).IsRequired();
            e.Property(x => x.BookedAt).HasDefaultValueSql("now()");

            e.HasIndex(x => x.RequestId, "ux_taxibookings_request_active")
             .IsUnique()
             .HasFilter($"\"Status\" IN ({BookingStates.ActiveSqlList})");
            // The overlap check of the confirm transaction.
            e.HasIndex(x => new { x.DriverListingId, x.DateFrom, x.DateTo }, "ix_taxibookings_driver_active")
             .HasFilter($"\"Status\" IN ({BookingStates.ActiveSqlList})");
            e.HasIndex(x => new { x.DriverListingId, x.DateFrom }, "ix_taxibookings_driver_dates")
             .IsDescending(false, true);
            e.HasIndex(x => new { x.TravellerUserId, x.BookedAt }, "ix_taxibookings_traveller")
             .IsDescending(false, true);
        });

        modelBuilder.Entity<TaxiRating>(e =>
        {
            e.ToTable("TaxiRatings", t =>
                t.HasCheckConstraint("ck_taxiratings_stars", "\"Stars\" BETWEEN 1 AND 5"));
            e.HasKey(x => x.BookingId);
            e.Property(x => x.BookingId).ValueGeneratedNever();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

            e.HasOne<TaxiBooking>().WithOne().HasForeignKey<TaxiRating>(x => x.BookingId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.DriverListingId, x.CreatedAt }, "ix_taxiratings_driver")
             .IsDescending(false, true);
        });

        modelBuilder.Entity<DriverDispatchState>(e =>
        {
            e.ToTable("DriverDispatchStates");
            e.HasKey(x => new { x.DriverListingId, x.CityKey });
            e.Property(x => x.Pass).HasDefaultValue(0L);
            e.Property(x => x.SeenSeq).HasDefaultValue(0L);
        });

        modelBuilder.Entity<TaxiDispatchCity>(e =>
        {
            e.ToTable("TaxiDispatchCities");
            e.HasKey(x => x.CityKey);
            e.Property(x => x.CityKey).ValueGeneratedNever();
            e.Property(x => x.Seq).HasDefaultValue(0L);
            e.Property(x => x.LastMinPass).HasDefaultValue(0L);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<DueAction>(e =>
        {
            e.ToTable("DueActions", t =>
            {
                t.HasCheckConstraint("ck_dueactions_kind", $"\"Kind\" IN ({DueActionKinds.SqlList})");
                t.HasCheckConstraint("ck_dueactions_state", $"\"State\" IN ({DueActionStates.SqlList})");
                t.HasCheckConstraint("ck_dueactions_done", TaxiSqlLists.DueActionDoneCheck);
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Kind).HasMaxLength(24).IsRequired();
            e.Property(x => x.RoundNo).HasDefaultValue(0);
            e.Property(x => x.State).HasMaxLength(10).IsRequired().HasDefaultValue(DueActionStates.Pending);
            e.Property(x => x.Attempts).HasDefaultValue((short)0);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

            e.HasIndex(x => new { x.SubjectId, x.Kind, x.RoundNo }, "ux_dueactions_subject").IsUnique();
            e.HasIndex(x => new { x.DueAt, x.Id }, "ix_dueactions_due")
             .HasFilter($"\"State\" = '{DueActionStates.Pending}'");
            e.HasIndex(x => x.LeaseUntil, "ix_dueactions_lease")
             .HasFilter($"\"State\" = '{DueActionStates.Running}'");
            e.HasIndex(x => x.RequestId, "ix_dueactions_request")
             .HasFilter($"\"State\" = '{DueActionStates.Pending}'");
            e.HasIndex(x => x.DoneAt, "ix_dueactions_done")
             .HasFilter("\"DoneAt\" IS NOT NULL");
        });
    }

    internal static class TaxiSqlLists
    {
        public static readonly string Dispatching = string.Join(", ", RequestStates.Dispatching.Select(s => $"'{s}'"));

        public static readonly string DueActionDoneCheck =
            $"(\"State\" IN ('{DueActionStates.Pending}','{DueActionStates.Running}')) = (\"DoneAt\" IS NULL)";
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Infrastructure.Data;

// Owner roles: Stay Owner and Taxi Driver (CONTRACT_BACKEND section 1). No DbSet properties: code uses Set<T>().
// Row versions are plain Version numbers (raw conditional updates). The three HiddenByPlan filters are the only query filters of the model.
public static class OwnerFeatureModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CalendarEntry>(e =>
        {
            e.ToTable("CalendarEntries", t =>
            {
                t.HasCheckConstraint("ck_calendarentries_listingkind", $"\"ListingKind\" IN ({OwnerListingKinds.SqlList})");
                t.HasCheckConstraint("ck_calendarentries_kind", $"\"Kind\" IN ({CalendarEntryKinds.SqlList})");
                t.HasCheckConstraint("ck_calendarentries_source",
                    "((\"Kind\" = 'Auto') = (\"SourceType\" IS NOT NULL AND \"SourceId\" IS NOT NULL)) " +
                    $"AND (\"SourceType\" IS NULL OR \"SourceType\" IN ({CalendarSourceTypes.SqlList}))");
                t.HasCheckConstraint("ck_calendarentries_dates", "\"DateTo\" >= \"DateFrom\"");
                t.HasCheckConstraint("ck_calendarentries_manual",
                    "\"Kind\" <> 'Manual' OR (length(btrim(\"Label\")) >= 1 AND NOT \"Cancelled\" " +
                    $"AND (\"ListingKind\" <> 'Stay' OR (\"DateTo\" > \"DateFrom\" AND \"DateTo\" - \"DateFrom\" <= {OwnerLimits.StayEntryMaxNights})) " +
                    "AND (\"ListingKind\" <> 'Taxi' OR \"DateTo\" = \"DateFrom\"))");
                t.HasCheckConstraint("ck_calendarentries_time", "\"TimeOfDay\" IS NULL OR (\"ListingKind\" = 'Taxi' AND \"Kind\" = 'Manual')");
                t.HasCheckConstraint("ck_calendarentries_final", $"\"FinalAmount\" IS NULL OR \"FinalAmount\" BETWEEN {OwnerLimits.MoneyMin} AND {OwnerLimits.MoneyMax}");
                t.HasCheckConstraint("ck_calendarentries_advance",
                    $"\"AdvanceReceived\" IS NULL OR (\"AdvanceReceived\" BETWEEN 0 AND {OwnerLimits.MoneyMax} " +
                    "AND (\"FinalAmount\" IS NULL OR \"AdvanceReceived\" <= \"FinalAmount\"))");
                t.HasCheckConstraint("ck_calendarentries_paid", "NOT \"PaidInFull\" OR \"FinalAmount\" IS NOT NULL");
                t.HasCheckConstraint("ck_calendarentries_cancelledby", $"\"CancelledBy\" IS NULL OR \"CancelledBy\" IN ({CalendarCancelledBy.SqlList})");
                t.HasCheckConstraint("ck_calendarentries_keepvisible", "NOT \"KeepVisible\" OR (\"ListingKind\" = 'Taxi' AND \"Kind\" = 'Manual')");
                t.HasCheckConstraint("ck_calendarentries_autosnap",
                    "\"Kind\" = 'Auto' OR (\"Guests\" IS NULL AND \"Seats\" IS NULL AND \"RoomNames\" IS NULL AND \"PlacesJson\" IS NULL AND \"CancelledBy\" IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.ListingKind).HasMaxLength(8).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(8).IsRequired();
            e.Property(x => x.SourceType).HasMaxLength(16);
            e.Property(x => x.DateFrom).HasColumnType("date");
            e.Property(x => x.DateTo).HasColumnType("date");
            e.Property(x => x.TimeOfDay).HasColumnType("time");
            e.Property(x => x.FromPlace).HasMaxLength(OwnerLimits.PlaceMax);
            e.Property(x => x.ToPlace).HasMaxLength(OwnerLimits.PlaceMax);
            e.Property(x => x.Label).HasMaxLength(OwnerLimits.LabelMax).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(OwnerLimits.DetailMax);
            e.Property(x => x.Note).HasMaxLength(OwnerLimits.NoteMax);
            e.Property(x => x.RoomNames).HasMaxLength(OwnerLimits.RoomNamesMax);
            e.Property(x => x.PlacesJson).HasColumnType("jsonb");
            e.Property(x => x.CancelledBy).HasMaxLength(12);
            e.Property(x => x.CancelledOn).HasColumnType("date");
            e.Property(x => x.PaidInFull).HasDefaultValue(false);
            e.Property(x => x.Cancelled).HasDefaultValue(false);
            e.Property(x => x.KeepVisible).HasDefaultValue(false);
            e.Property(x => x.Version).HasDefaultValue(1).HasSentinel(1);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.TravellerUserId).IsRequired(false).OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.SourceType, x.SourceId }, "ux_calendarentries_source").IsUnique().HasFilter("\"Kind\" = 'Auto'");
            e.HasIndex(x => new { x.ListingKind, x.ListingId, x.DateTo, x.Id }, "ix_calendarentries_listing_dateto")
             .IncludeProperties(x => new { x.DateFrom, x.Kind, x.Cancelled, x.KeepVisible });
            e.HasIndex(x => new { x.ListingKind, x.ListingId, x.DateFrom, x.Id }, "ix_calendarentries_listing_datefrom");
            // FB-62: ix_calendarentries_manual_active is gone (ix_calendarentries_listing_dateto serves the same scans).
            e.HasIndex(x => x.DateTo, "ix_calendarentries_dateto");
            e.HasIndex(x => x.TravellerUserId, "ix_calendarentries_traveller").HasFilter("\"TravellerUserId\" IS NOT NULL");
            e.HasIndex(x => x.OwnerUserId, "ix_calendarentries_owner");
        });

        modelBuilder.Entity<StayBlockedRange>(e =>
        {
            e.ToTable("StayBlockedRanges", t =>
                t.HasCheckConstraint("ck_stayblockedranges_dates",
                    $"\"DateTo\" >= \"DateFrom\" AND \"DateTo\" - \"DateFrom\" <= {OwnerLimits.BlockMaxNights - 1}"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.DateFrom).HasColumnType("date");
            e.Property(x => x.DateTo).HasColumnType("date");
            e.Property(x => x.Reason).HasMaxLength(OwnerLimits.ReasonMax);
            e.Property(x => x.Version).HasDefaultValue(1).HasSentinel(1);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne<StayListing>().WithMany().HasForeignKey(x => x.StayListingId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.StayListingId, x.DateTo, x.Id }, "ix_stayblockedranges_stay_dateto").IncludeProperties(x => x.DateFrom);
            e.HasIndex(x => x.DateTo, "ix_stayblockedranges_dateto");
        });

        modelBuilder.Entity<TaxiDayOff>(e =>
        {
            e.ToTable("TaxiDayOffs", t =>
                t.HasCheckConstraint("ck_taxidayoffs_reason",
                    $"\"Reason\" IS NULL OR char_length(\"Reason\") BETWEEN 1 AND {OwnerLimits.TaxiDayOffReasonMax}"));
            e.HasKey(x => new { x.TaxiListingId, x.Date });
            e.Property(x => x.Date).HasColumnType("date");
            e.Property(x => x.Reason).HasMaxLength(OwnerLimits.TaxiDayOffReasonMax);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne<TaxiListing>().WithMany().HasForeignKey(x => x.TaxiListingId).OnDelete(DeleteBehavior.Cascade);

            // Retention order and the "all days off of date D" side of the list anti-joins.
            e.HasIndex(x => new { x.Date, x.TaxiListingId }, "ix_taxidayoffs_date");
        });

        modelBuilder.Entity<StaySpecialPrice>(e =>
        {
            e.ToTable("StaySpecialPrices", t =>
            {
                t.HasCheckConstraint("ck_stayspecialprices_direction", $"\"Direction\" IN ({SpecialPriceDirections.SqlList})");
                t.HasCheckConstraint("ck_stayspecialprices_percent",
                    "\"Percent\" % 5 = 0 AND ((\"Direction\" = 'Lower' AND \"Percent\" BETWEEN 5 AND 50) OR (\"Direction\" = 'Higher' AND \"Percent\" BETWEEN 5 AND 100))");
                t.HasCheckConstraint("ck_stayspecialprices_dates",
                    $"\"DateTo\" >= \"DateFrom\" AND \"DateTo\" - \"DateFrom\" <= {OwnerLimits.SpecialMaxNights - 1}");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.DateFrom).HasColumnType("date");
            e.Property(x => x.DateTo).HasColumnType("date");
            e.Property(x => x.Direction).HasMaxLength(6).IsRequired();
            e.Property(x => x.LabelCopy).HasMaxLength(OwnerLimits.FestivalLabelMax);
            e.Property(x => x.IsPaused).HasDefaultValue(false);
            e.Property(x => x.PausedByPlan).HasDefaultValue(false);
            e.Property(x => x.Version).HasDefaultValue(1).HasSentinel(1);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            e.HasOne<StayListing>().WithMany().HasForeignKey(x => x.StayListingId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.StayListingId, x.DateTo, x.Id }, "ix_stayspecialprices_stay_dateto");
            e.HasIndex(x => x.DateTo, "ix_stayspecialprices_dateto");
            // FB-62: ix_stayspecialprices_bigoff is gone (ix_stayspecialprices_stay_dateto serves the per-stay read).
            e.HasIndex(x => new { x.DateFrom, x.Id }, "ix_stayspecialprices_active_from")
             .HasFilter("NOT \"IsPaused\" AND NOT \"PausedByPlan\"");
        });

        modelBuilder.Entity<FestivalDate>(e =>
        {
            e.ToTable("FestivalDates", t =>
            {
                t.HasCheckConstraint("ck_festivaldates_dates",
                    $"\"DateTo\" >= \"DateFrom\" AND \"DateTo\" - \"DateFrom\" <= {OwnerLimits.FestivalMaxDays - 1}");
                t.HasCheckConstraint("ck_festivaldates_name", "length(btrim(\"Name\")) >= 1");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Name).HasMaxLength(OwnerLimits.FestivalNameMax).IsRequired();
            e.Property(x => x.DateFrom).HasColumnType("date");
            e.Property(x => x.DateTo).HasColumnType("date");
            e.Property(x => x.StateName).HasMaxLength(OwnerLimits.StateNameMax);
            e.Property(x => x.IsActive).HasDefaultValue(true).HasSentinel(true);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

            // FB-62: ix_festivaldates_active_from and ix_festivaldates_from_id are gone (small table, read through FestivalCache).
        });

        modelBuilder.Entity<FestivalReminderSent>(e =>
        {
            e.ToTable("FestivalReminderSent");
            e.HasKey(x => new { x.UserId, x.FestivalDateId });
            e.Property(x => x.FestivalDateTo).HasColumnType("date");
            e.Property(x => x.SentAt).HasDefaultValueSql("now()");

            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FestivalDate>().WithMany().HasForeignKey(x => x.FestivalDateId).OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.FestivalDateTo, "ix_festivalremindersent_dateto");
            e.HasIndex(x => x.FestivalDateId, "ix_festivalremindersent_festival");
        });

        modelBuilder.Entity<OwnerReminderRun>(e =>
        {
            e.ToTable("OwnerReminderRuns", t =>
                t.HasCheckConstraint("ck_ownerreminderruns_listingkind", $"\"ListingKind\" IN ({OwnerListingKinds.SqlList})"));
            e.HasKey(x => new { x.RunDate, x.ListingKind });
            e.Property(x => x.RunDate).HasColumnType("date");
            e.Property(x => x.ListingKind).HasMaxLength(8);
            e.Property(x => x.StartedAt).HasDefaultValueSql("now()");
            e.Property(x => x.Sent).HasDefaultValue(0);
        });

        modelBuilder.Entity<OwnerWeeklyStat>(e =>
        {
            e.ToTable("OwnerWeeklyStats", t =>
                t.HasCheckConstraint("ck_ownerweeklystats_listingkind", $"\"ListingKind\" IN ({OwnerListingKinds.SqlList})"));
            e.HasKey(x => new { x.ListingKind, x.ListingId, x.Day });
            e.Property(x => x.ListingKind).HasMaxLength(8);
            e.Property(x => x.Day).HasColumnType("date");
            e.Property(x => x.Shown).HasDefaultValue(0);
            e.Property(x => x.RequestsNoRoute).HasDefaultValue(0);

            e.HasIndex(x => x.Day, "ix_ownerweeklystats_day");
        });

        ConfigureChangedTables(modelBuilder);
    }

    private static void ConfigureChangedTables(ModelBuilder modelBuilder)
    {
        // Hidden, never deleted (contract 3.3). Every LINQ read sees visible rows only; IgnoreQueryFilters() lifts only this rule.
        modelBuilder.Entity<StayPhoto>(e =>
        {
            e.Property(x => x.HiddenByPlan).HasDefaultValue(false);
            e.HasQueryFilter(x => !x.HiddenByPlan);
        });
        modelBuilder.Entity<StayRoom>(e =>
        {
            e.Property(x => x.HiddenByPlan).HasDefaultValue(false);
            e.HasQueryFilter(x => !x.HiddenByPlan);
        });
        modelBuilder.Entity<TaxiRoute>(e =>
        {
            e.Property(x => x.HiddenByPlan).HasDefaultValue(false);
            e.HasQueryFilter(x => !x.HiddenByPlan);
        });

        modelBuilder.Entity<StayListing>(e =>
        {
            e.Property(x => x.PendingEditJson).HasColumnType("jsonb");
            e.Property(x => x.PendingEditRejectReason).HasMaxLength(OwnerLimits.PendingEditReasonMax);
            e.Property(x => x.PendingEditResult).HasMaxLength(8);
            e.Property(x => x.SpecialPriceSavesDay).HasColumnType("date");
            e.Property(x => x.SpecialPriceSavesCount).HasDefaultValue((short)0);
            e.Property(x => x.PlanVisibleSavesDay).HasColumnType("date");
            e.Property(x => x.PlanVisibleSavesCount).HasDefaultValue((short)0);
            e.Property(x => x.RemindersOn).HasDefaultValue(true).HasSentinel(true);
            e.Property(x => x.EnquiryRingOn).HasDefaultValue(true).HasSentinel(true);
            e.ToTable(t => t.HasCheckConstraint("ck_staylistings_pendingeditresult",
                $"\"PendingEditResult\" IS NULL OR \"PendingEditResult\" IN ({PendingEditResults.SqlList})"));
            e.HasIndex(x => new { x.PendingEditAt, x.Id }, "ix_staylistings_pendingedit")
             .HasFilter("\"PendingEditJson\" IS NOT NULL AND \"IsDeleted\" = false");
        });

        modelBuilder.Entity<TaxiListing>(e =>
        {
            e.Property(x => x.RingWeight).HasDefaultValue((short)1).HasSentinel((short)1);
            e.Property(x => x.PlanVisibleSavesDay).HasColumnType("date");
            e.Property(x => x.PlanVisibleSavesCount).HasDefaultValue((short)0);
            e.Property(x => x.RemindersOn).HasDefaultValue(true).HasSentinel(true);
            e.ToTable(t => t.HasCheckConstraint("ck_taxilistings_ringweight", "\"RingWeight\" BETWEEN 1 AND 5"));
        });

        modelBuilder.Entity<TripEnquiry>(e =>
        {
            e.Property(x => x.ListedStayBreakdownJson).HasColumnType("jsonb");
            e.Property(x => x.CompletionCountedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
            e.ToTable(t => t.HasCheckConstraint("ck_tripenquiries_listedstaytotal",
                $"\"ListedStayTotal\" IS NULL OR (\"Kind\" = 'Stay' AND \"ListedStayTotal\" >= {OwnerLimits.ListedStayTotalMin})"));
        });

        modelBuilder.Entity<AppFeatureFlag>(e => e.Property(x => x.AnnounceDate).HasColumnType("date"));

        // Stats flush (RequestsNoRoute) and the Home AddRoutes example read taxi requests by creation day (AU-31).
        modelBuilder.Entity<TaxiRequest>()
            .HasIndex(x => x.CreatedAt, "ix_taxirequests_created")
            .IncludeProperties(x => new { x.MatchFromCityKey, x.MatchToCityKey });

        // FB-59: the Home suggestion and the nightly RequestsNoRoute read requests of one pickup city by day; routes by from city.
        modelBuilder.Entity<TaxiRequest>()
            .HasIndex(x => new { x.MatchFromCityKey, x.CreatedAt }, "ix_taxirequests_from_created")
            .IncludeProperties(x => new { x.MatchToCityKey, x.DropCityKey });
        modelBuilder.Entity<TaxiRoute>()
            .HasIndex(x => x.FromCityKey, "ix_taxiroutes_from_city")
            .IncludeProperties(x => x.TaxiListingId)
            .HasFilter("\"FromCityKey\" IS NOT NULL AND NOT \"HiddenByPlan\"");

        // FB-31: the 15-minute calendar repair reads only recently changed sources.
        modelBuilder.Entity<TripEnquiry>()
            .HasIndex(x => x.UpdatedAt, "ix_tripenquiries_confirmed_updated")
            .HasFilter($"\"Status\" = '{TripEnquiryStatuses.ConfirmedByBoth}'");
        modelBuilder.Entity<TaxiBooking>()
            .HasIndex(x => x.BookedAt, "ix_taxibookings_booked");
        modelBuilder.Entity<TaxiBooking>()
            .HasIndex(x => x.CancelledAt, "ix_taxibookings_cancelled")
            .HasFilter($"\"Status\" = '{BookingStates.Cancelled}'");
    }
}

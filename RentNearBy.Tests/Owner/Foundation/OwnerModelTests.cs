using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Owner.Foundation;

// Contract 1.1, 1.2, 1.6, AU-31, R-1, R-8: every table, column, check, index, filter and include of the owner schema; one migration;
// the three HiddenByPlan filters are the only query filters of the model.
public class OwnerModelTests
{
    private static ApplicationDbContext NewContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite())
        .Options);

    private static (IModel Model, IRelationalModel Relational) Load()
    {
        using var db = NewContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        return (model, model.GetRelationalModel());
    }

    private static ITable Table(string name) => Load().Relational.Tables.Single(t => t.Name == name);

    private static ITableIndex Index(string table, string name)
        => Table(table).Indexes.SingleOrDefault(i => i.Name == name) ?? throw new Xunit.Sdk.XunitException($"No index {name} on {table}");

    private static string Check(string table, string name)
        => Load().Model.GetEntityTypes().Where(e => e.GetTableName() == table).SelectMany(e => e.GetCheckConstraints())
            .SingleOrDefault(c => c.Name == name)?.Sql ?? throw new Xunit.Sdk.XunitException($"No check {name} on {table}");

    [Theory]
    [InlineData("CalendarEntries", "Id,OwnerUserId,ListingKind,ListingId,Kind,SourceType,SourceId,TravellerUserId,DateFrom,DateTo,TimeOfDay,FromPlace,ToPlace,Label,Detail,Note,ListedTotal,AgreedTotal,Guests,Seats,RoomNames,PlacesJson,CancelledBy,CancelledOn,FinalAmount,AdvanceReceived,PaidInFull,Cancelled,Version,CreatedAt,UpdatedAt")]
    [InlineData("StayBlockedRanges", "Id,StayListingId,DateFrom,DateTo,Reason,Version,CreatedAt,UpdatedAt")]
    [InlineData("StaySpecialPrices", "Id,StayListingId,DateFrom,DateTo,Direction,Percent,FestivalDateId,LabelCopy,IsPaused,PausedByPlan,Version,CreatedAt,UpdatedAt")]
    [InlineData("FestivalDates", "Id,Name,DateFrom,DateTo,StateName,IsActive,CreatedAt,UpdatedAt")]
    [InlineData("FestivalReminderSent", "UserId,FestivalDateId,FestivalDateTo,SentAt")]
    [InlineData("OwnerReminderRuns", "RunDate,ListingKind,StartedAt,FinishedAt,LastListingId,Sent")]
    [InlineData("OwnerWeeklyStats", "ListingKind,ListingId,Day,Shown,RequestsNoRoute")]
    public void New_tables_have_exactly_their_columns(string table, string columns)
        => Assert.Equal(columns.Split(',').OrderBy(c => c), Table(table).Columns.Select(c => c.Name).OrderBy(c => c));

    [Theory]
    [InlineData("StayPhotos", "HiddenByPlan")]
    [InlineData("StayRooms", "HiddenByPlan")]
    [InlineData("TaxiRoutes", "HiddenByPlan")]
    [InlineData("StayListings", "PendingEditJson,PendingEditAt,PendingEditRejectReason,PendingEditResult,PendingEditResultAt,SpecialPriceSavesDay,SpecialPriceSavesCount,PlanVisibleSavesDay,PlanVisibleSavesCount,RemindersOn")]
    [InlineData("TaxiListings", "RingWeight,PlanVisibleSavesDay,PlanVisibleSavesCount,RemindersOn")]
    [InlineData("TripEnquiries", "ListedStayTotal,ListedStayBreakdownJson")]
    [InlineData("AppFeatureFlags", "AnnounceDate,PaymentEnabledAt")]
    public void Changed_tables_gain_their_columns(string table, string columns)
    {
        var t = Table(table);
        Assert.All(columns.Split(','), c => Assert.NotNull(t.FindColumn(c)));
    }

    [Fact]
    public void Column_types_and_defaults()
    {
        var e = Table("CalendarEntries");
        Assert.Equal("date", e.FindColumn("DateFrom")!.StoreType);
        Assert.Equal("time", e.FindColumn("TimeOfDay")!.StoreType);
        Assert.Equal("jsonb", e.FindColumn("PlacesJson")!.StoreType);
        Assert.Equal("character varying(60)", e.FindColumn("Label")!.StoreType);
        Assert.Equal("character varying(200)", e.FindColumn("Note")!.StoreType);
        Assert.Equal("character varying(80)", e.FindColumn("FromPlace")!.StoreType);
        Assert.Equal(1, e.FindColumn("Version")!.DefaultValue);
        Assert.Equal("jsonb", Table("StayListings").FindColumn("PendingEditJson")!.StoreType);
        Assert.Equal("jsonb", Table("TripEnquiries").FindColumn("ListedStayBreakdownJson")!.StoreType);
        Assert.Equal((short)1, Table("TaxiListings").FindColumn("RingWeight")!.DefaultValue);
        Assert.Equal(true, Table("TaxiListings").FindColumn("RemindersOn")!.DefaultValue);
        Assert.Equal(true, Table("StayListings").FindColumn("RemindersOn")!.DefaultValue);
        Assert.Equal(false, Table("StayRooms").FindColumn("HiddenByPlan")!.DefaultValue);
        Assert.Equal("date", Table("AppFeatureFlags").FindColumn("AnnounceDate")!.StoreType);
        Assert.Equal("character varying(100)", Table("FestivalDates").FindColumn("StateName")!.StoreType);
    }

    [Theory]
    [InlineData("CalendarEntries", "ck_calendarentries_listingkind", "\"ListingKind\" IN ('Stay', 'Taxi')")]
    [InlineData("CalendarEntries", "ck_calendarentries_kind", "\"Kind\" IN ('Auto', 'Manual')")]
    [InlineData("CalendarEntries", "ck_calendarentries_dates", "\"DateTo\" >= \"DateFrom\"")]
    [InlineData("CalendarEntries", "ck_calendarentries_time", "\"TimeOfDay\" IS NULL OR (\"ListingKind\" = 'Taxi' AND \"Kind\" = 'Manual')")]
    [InlineData("CalendarEntries", "ck_calendarentries_final", "\"FinalAmount\" IS NULL OR \"FinalAmount\" BETWEEN 1 AND 9900000")]
    [InlineData("CalendarEntries", "ck_calendarentries_paid", "NOT \"PaidInFull\" OR \"FinalAmount\" IS NOT NULL")]
    [InlineData("StayBlockedRanges", "ck_stayblockedranges_dates", "\"DateTo\" >= \"DateFrom\" AND \"DateTo\" - \"DateFrom\" <= 89")]
    [InlineData("StaySpecialPrices", "ck_stayspecialprices_direction", "\"Direction\" IN ('Lower', 'Higher')")]
    [InlineData("StaySpecialPrices", "ck_stayspecialprices_dates", "\"DateTo\" >= \"DateFrom\" AND \"DateTo\" - \"DateFrom\" <= 59")]
    [InlineData("FestivalDates", "ck_festivaldates_dates", "\"DateTo\" >= \"DateFrom\" AND \"DateTo\" - \"DateFrom\" <= 29")]
    [InlineData("FestivalDates", "ck_festivaldates_name", "length(btrim(\"Name\")) >= 1")]
    [InlineData("TaxiListings", "ck_taxilistings_ringweight", "\"RingWeight\" BETWEEN 1 AND 5")]
    [InlineData("TripEnquiries", "ck_tripenquiries_listedstaytotal", "\"ListedStayTotal\" IS NULL OR (\"Kind\" = 'Stay' AND \"ListedStayTotal\" >= 100)")]
    [InlineData("StayListings", "ck_staylistings_pendingeditresult", "\"PendingEditResult\" IS NULL OR \"PendingEditResult\" IN ('Approved', 'Rejected')")]
    public void Check_constraints_are_exact(string table, string name, string sql)
        => Assert.Equal(sql, Check(table, name));

    [Fact]
    public void Calendar_entry_rules_hold_in_the_checks()
    {
        Assert.Contains("(\"Kind\" = 'Auto') = (\"SourceType\" IS NOT NULL AND \"SourceId\" IS NOT NULL)", Check("CalendarEntries", "ck_calendarentries_source"));
        Assert.Contains("'StayEnquiry', 'TaxiBooking', 'TaxiLead'", Check("CalendarEntries", "ck_calendarentries_source"));
        var manual = Check("CalendarEntries", "ck_calendarentries_manual");
        Assert.Contains("length(btrim(\"Label\")) >= 1 AND NOT \"Cancelled\"", manual);
        Assert.Contains("\"DateTo\" - \"DateFrom\" <= 60", manual);
        Assert.Contains("\"ListingKind\" <> 'Taxi' OR \"DateTo\" = \"DateFrom\"", manual);
        Assert.Contains("\"AdvanceReceived\" <= \"FinalAmount\"", Check("CalendarEntries", "ck_calendarentries_advance"));
        Assert.Contains("'Traveller', 'Owner', 'Driver', 'Admin', 'System'", Check("CalendarEntries", "ck_calendarentries_cancelledby"));
        Assert.StartsWith("\"Kind\" = 'Auto' OR (\"Guests\" IS NULL", Check("CalendarEntries", "ck_calendarentries_autosnap"));
        Assert.Contains("\"Percent\" % 5 = 0", Check("StaySpecialPrices", "ck_stayspecialprices_percent"));
        Assert.Contains("BETWEEN 5 AND 100", Check("StaySpecialPrices", "ck_stayspecialprices_percent"));
        Assert.Contains("'Stay', 'Taxi'", Check("OwnerReminderRuns", "ck_ownerreminderruns_listingkind"));
        Assert.Contains("'Stay', 'Taxi'", Check("OwnerWeeklyStats", "ck_ownerweeklystats_listingkind"));
    }

    [Theory]
    [InlineData("CalendarEntries", "ux_calendarentries_source", "SourceType,SourceId", true, "\"Kind\" = 'Auto'")]
    [InlineData("CalendarEntries", "ix_calendarentries_listing_dateto", "ListingKind,ListingId,DateTo,Id", false, null)]
    [InlineData("CalendarEntries", "ix_calendarentries_listing_datefrom", "ListingKind,ListingId,DateFrom,Id", false, null)]
    [InlineData("CalendarEntries", "ix_calendarentries_manual_active", "ListingKind,ListingId,DateTo,Id", false, "\"Kind\" = 'Manual'")]
    [InlineData("CalendarEntries", "ix_calendarentries_dateto", "DateTo", false, null)]
    [InlineData("CalendarEntries", "ix_calendarentries_traveller", "TravellerUserId", false, "\"TravellerUserId\" IS NOT NULL")]
    [InlineData("CalendarEntries", "ix_calendarentries_owner", "OwnerUserId", false, null)]
    [InlineData("StayBlockedRanges", "ix_stayblockedranges_stay_dateto", "StayListingId,DateTo,Id", false, null)]
    [InlineData("StayBlockedRanges", "ix_stayblockedranges_dateto", "DateTo", false, null)]
    [InlineData("StaySpecialPrices", "ix_stayspecialprices_stay_dateto", "StayListingId,DateTo,Id", false, null)]
    [InlineData("StaySpecialPrices", "ix_stayspecialprices_dateto", "DateTo", false, null)]
    [InlineData("StaySpecialPrices", "ix_stayspecialprices_bigoff", "StayListingId,DateTo", false, "\"Direction\" = 'Lower' AND \"Percent\" >= 40 AND NOT \"IsPaused\" AND NOT \"PausedByPlan\"")]
    [InlineData("StaySpecialPrices", "ix_stayspecialprices_active_from", "DateFrom,Id", false, "NOT \"IsPaused\" AND NOT \"PausedByPlan\"")]
    [InlineData("FestivalDates", "ix_festivaldates_active_from", "IsActive,DateFrom", false, null)]
    [InlineData("FestivalDates", "ix_festivaldates_from_id", "DateFrom,Id", false, null)]
    [InlineData("FestivalReminderSent", "ix_festivalremindersent_dateto", "FestivalDateTo", false, null)]
    [InlineData("FestivalReminderSent", "ix_festivalremindersent_festival", "FestivalDateId", false, null)]
    [InlineData("OwnerWeeklyStats", "ix_ownerweeklystats_day", "Day", false, null)]
    [InlineData("StayListings", "ix_staylistings_pendingedit", "PendingEditAt,Id", false, "\"PendingEditJson\" IS NOT NULL AND \"IsDeleted\" = false")]
    [InlineData("TaxiRequests", "ix_taxirequests_created", "CreatedAt", false, null)]
    [InlineData("TaxiRoutes", "ix_taxiroutes_to_from_city", "ToCityKey,FromCityKey", false, "\"ToCityKey\" IS NOT NULL AND \"FromCityKey\" IS NOT NULL AND NOT \"HiddenByPlan\"")]
    public void Indexes_are_exact(string table, string name, string columns, bool unique, string? filter)
    {
        var ix = Index(table, name);
        Assert.Equal(columns.Split(','), ix.Columns.Select(c => c.Name));
        Assert.Equal(unique, ix.IsUnique);
        Assert.Equal(filter, ix.Filter);
    }

    [Fact]
    public void Includes_and_sort_orders()
    {
        var includes = (string[]?)Index("CalendarEntries", "ix_calendarentries_listing_dateto").MappedIndexes.Single()["Npgsql:IndexInclude"];
        Assert.Equal(["DateFrom", "Kind", "Cancelled"], includes);
        Assert.Equal(["DateFrom"], (string[]?)Index("StayBlockedRanges", "ix_stayblockedranges_stay_dateto").MappedIndexes.Single()["Npgsql:IndexInclude"]);
        Assert.Equal(["MatchFromCityKey", "MatchToCityKey"], (string[]?)Index("TaxiRequests", "ix_taxirequests_created").MappedIndexes.Single()["Npgsql:IndexInclude"]);
        Assert.Equal([true, false], Index("FestivalDates", "ix_festivaldates_from_id").MappedIndexes.Single().IsDescending);
    }

    [Fact]
    public void Keys_and_foreign_keys()
    {
        var model = Load().Model;
        Assert.Equal(["UserId", "FestivalDateId"], model.FindEntityType(typeof(FestivalReminderSent))!.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(["RunDate", "ListingKind"], model.FindEntityType(typeof(OwnerReminderRun))!.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(["ListingKind", "ListingId", "Day"], model.FindEntityType(typeof(OwnerWeeklyStat))!.FindPrimaryKey()!.Properties.Select(p => p.Name));
        var entry = model.FindEntityType(typeof(CalendarEntry))!;
        Assert.Equal(DeleteBehavior.Cascade, entry.GetForeignKeys().Single(f => f.Properties[0].Name == "OwnerUserId").DeleteBehavior);
        Assert.Equal(DeleteBehavior.SetNull, entry.GetForeignKeys().Single(f => f.Properties[0].Name == "TravellerUserId").DeleteBehavior);
        Assert.DoesNotContain(entry.GetForeignKeys(), f => f.Properties[0].Name is "ListingId" or "SourceId");
        Assert.Empty(model.FindEntityType(typeof(StaySpecialPrice))!.GetForeignKeys().Where(f => f.Properties[0].Name == "FestivalDateId"));
        Assert.Equal(DeleteBehavior.Cascade, model.FindEntityType(typeof(StayBlockedRange))!.GetForeignKeys().Single().DeleteBehavior);
        Assert.Equal(2, model.FindEntityType(typeof(FestivalReminderSent))!.GetForeignKeys().Count(f => f.DeleteBehavior == DeleteBehavior.Cascade));
    }

    [Fact]
    public void Only_the_three_hidden_by_plan_filters_exist()
    {
        var filtered = Load().Model.GetEntityTypes().Where(e => e.GetQueryFilter() != null)
            .Select(e => e.ClrType.Name).OrderBy(n => n);
        Assert.Equal(["StayPhoto", "StayRoom", "TaxiRoute"], filtered);
    }

    [Fact]
    public void User_notice_kinds_check_holds_the_five_owner_kinds()
    {
        Assert.Equal(["StayEditApproved", "StayEditRejected", "FestivalReminder", "PlanLimitsChanged", "PlansStarting"], NoticeKinds.Owner);
        var sql = Check("UserNotices", "ck_usernotices_kind");
        Assert.All(NoticeKinds.Owner, k => Assert.Contains($"'{k}'", sql));
        Assert.All(NoticeKinds.Owner, k => Assert.True(k.Length <= 20));
    }

    [Fact]
    public void Offer_end_reasons_hold_the_two_owner_reasons_inside_the_column_size()
    {
        Assert.Contains("RoutePausedByPlan", RentNearBy.Core.Models.TaxiRequests.OfferEndReasons.All);
        Assert.Contains("ListingInReview", RentNearBy.Core.Models.TaxiRequests.OfferEndReasons.All);
        Assert.All(RentNearBy.Core.Models.TaxiRequests.OfferEndReasons.All, r => Assert.True(r.Length <= 32));
    }

    [Fact]
    public void Exactly_one_migration_exists_and_it_has_every_owner_table()
    {
        using var db = NewContext();
        var assembly = db.GetService<IMigrationsAssembly>();
        var id = Assert.Single(assembly.Migrations).Key;
        Assert.EndsWith("_InitialCreate", id);
        var snapshot = assembly.ModelSnapshot!.Model;
        var tables = snapshot.GetEntityTypes().Select(e => e.GetTableName()).ToHashSet();
        Assert.All(new[] { "CalendarEntries", "StayBlockedRanges", "StaySpecialPrices", "FestivalDates", "FestivalReminderSent", "OwnerReminderRuns", "OwnerWeeklyStats" },
            t => Assert.Contains(t, tables));
    }
}

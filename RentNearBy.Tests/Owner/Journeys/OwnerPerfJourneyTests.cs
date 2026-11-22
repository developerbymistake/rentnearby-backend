using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// JT-B-PERF-1 (model part, runs here): indexes added and dropped by FB-31, FB-59, FB-62, FB-W4 and the counter columns.
public class OwnerPerfJourneyTests
{
    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only", o => o.UseNetTopologySuite())
            .Options;
        using var db = new ApplicationDbContext(options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static IEnumerable<string> IndexNames(IModel model, string table)
        => model.GetRelationalModel().Tables.Single(t => t.Name == table).Indexes.Select(i => i.Name);

    [Theory]
    [InlineData("TripEnquiries", "ix_tripenquiries_confirmed_updated")]
    [InlineData("TaxiBookings", "ix_taxibookings_booked")]
    [InlineData("TaxiBookings", "ix_taxibookings_cancelled")]
    [InlineData("TaxiBookings", "ix_taxibookings_trip")]
    [InlineData("TaxiRequests", "ix_taxirequests_from_created")]
    [InlineData("TaxiRoutes", "ix_taxiroutes_from_city")]
    public void Added_indexes_exist(string table, string name) => Assert.Contains(name, IndexNames(Model(), table));

    [Theory]
    [InlineData("CalendarEntries", "ix_calendarentries_manual_active")]
    [InlineData("StaySpecialPrices", "ix_stayspecialprices_bigoff")]
    [InlineData("FestivalDates", "ix_festivaldates_active_from")]
    [InlineData("FestivalDates", "ix_festivaldates_from_id")]
    [InlineData("DeviceTokens", "IX_DeviceTokens_UserId")]
    [InlineData("AdminDeviceTokens", "IX_AdminDeviceTokens_AdminId")]
    [InlineData("AdminDeviceTokens", "IX_AdminDeviceTokens_IsValid")]
    [InlineData("NotificationLogs", "IX_NotificationLogs_UserId")]
    [InlineData("ListingReports", "IX_ListingReports_Status")]
    public void Dropped_indexes_are_gone(string table, string name) => Assert.DoesNotContain(name, IndexNames(Model(), table));

    [Theory]
    [InlineData("StaySpecialPrices", "ix_stayspecialprices_active_from")]
    [InlineData("StayListings", "IX_StayListings_UserId_IsDeleted")]
    [InlineData("TaxiListings", "IX_TaxiListings_UserId_IsDeleted")]
    public void Kept_indexes_stay(string table, string name) => Assert.Contains(name, IndexNames(Model(), table));

    [Theory]
    [InlineData("Users", "CompletedTripsCount")]
    [InlineData("Users", "CompletedBookingsCount")]
    [InlineData("Trips", "CompletionCountedAt")]
    [InlineData("TripEnquiries", "CompletionCountedAt")]
    [InlineData("TaxiBookings", "CompletionCountedAt")]
    [InlineData("Sessions", "RevokeReason")]
    public void New_columns_are_in_the_model(string table, string column)
        => Assert.Contains(column, Model().GetRelationalModel().Tables.Single(t => t.Name == table).Columns.Select(c => c.Name));

    [Fact]
    public void Counter_checks_are_in_the_model()
    {
        var checks = Model().GetEntityTypes().Where(e => e.GetTableName() == "Users").SelectMany(e => e.GetCheckConstraints()).Select(c => c.Name).ToList();
        Assert.Contains("ck_users_completedtrips_nonneg", checks);
        Assert.Contains("ck_users_completedbookings_nonneg", checks);
    }

    [Fact]
    public void Calendar_repair_runs_bounded_every_15_minutes_and_full_once_a_night()
    {
        var src = File.ReadAllText(Path.Combine(CompletionCounterGuardTests.Root(), "RentNearBy.Infrastructure", "Services", "Owner", "Be1", "OwnerCalendarRepairJob.cs"));
        Assert.Contains("RepairAsync(full: false, ct)", src);
        Assert.Contains("RepairAsync(full: true, ct)", src);
        Assert.Contains("@since", RentNearBy.Infrastructure.Services.Owner.Be1.OwnerCalendarRepairJob.MissingEnquiriesRecentSql);
        Assert.Contains("@since", RentNearBy.Infrastructure.Services.Owner.Be1.OwnerCalendarRepairJob.MissingBookingsRecentSql);
        Assert.Contains("@since", RentNearBy.Infrastructure.Services.Owner.Be1.OwnerCalendarRepairJob.StaleEnquiriesRecentSql);
        Assert.Contains("@since", RentNearBy.Infrastructure.Services.Owner.Be1.OwnerCalendarRepairJob.StaleBookingsRecentSql);
        Assert.Contains("OwnerCalendarRepairNightlyJob", File.ReadAllText(Path.Combine(CompletionCounterGuardTests.Root(), "RentNearBy.Api", "Extensions", "Owner", "OwnerBe1Registration.cs")));
    }
}

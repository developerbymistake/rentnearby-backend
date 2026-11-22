using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Owner;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// JT-B-LC-6 (FB-25, D-15): a plate change sends a once-approved taxi back to review; its accepted offers can still be selected and
// confirmed, new rings stop, and a rejected re-review closes the open offers while bookings stay.
public class VehicleReviewJourneyTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    [Theory]
    [InlineData(TaxiStatuses.Approved, false, false, true)]
    [InlineData(TaxiStatuses.Approved, false, true, true)]
    [InlineData(TaxiStatuses.PendingReview, false, true, true)]
    [InlineData(TaxiStatuses.PendingReview, false, false, false)]
    [InlineData(TaxiStatuses.Rejected, false, true, false)]
    [InlineData(TaxiStatuses.Approved, true, true, false)]
    public void Existing_offer_can_be_chosen_during_a_re_review_only(string status, bool deleted, bool everApproved, bool expected)
    {
        Assert.Equal(expected, TaxiBookableRules.CanSelectExisting(status, deleted, everApproved));
        Assert.Equal(expected, new DriverLockRow(Guid.NewGuid(), Guid.NewGuid(), status, deleted, everApproved).IsBookable);
    }

    [Fact]
    public void New_rings_stay_approved_only()
    {
        Assert.True(TaxiBookableRules.CanRing(TaxiStatuses.Approved, false));
        Assert.False(TaxiBookableRules.CanRing(TaxiStatuses.PendingReview, false));
        Assert.DoesNotContain("PendingReview", TaxiOfferRows.DriverBookableSql);
    }

    // SQL parity: the select statement carries the same three arms as the C# rule; the lock row reads EverApproved.
    [Fact]
    public void Select_sql_mirrors_the_rule()
    {
        Assert.Contains($"l.\"Status\" = '{TaxiStatuses.Approved}' OR (l.\"Status\" = '{TaxiStatuses.PendingReview}' AND l.\"EverApproved\")", TaxiOfferRows.DriverSelectableSql);
        Assert.Contains("NOT l.\"IsDeleted\"", TaxiOfferRows.DriverSelectableSql);
        Assert.Contains("\"EverApproved\"", DriverCalendar.LockDriverSql);
        Assert.Contains("TaxiOfferRows.IsDriverSelectableAsync", Src("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiRequestService.cs"));
    }

    [Fact]
    public void Rejected_re_review_closes_offers_before_the_listing_write()
    {
        var src = Src("RentNearBy.Api", "Handlers", "AdminTaxiHandlers.cs");
        var body = src[src.IndexOf("public static async Task<IResult> RejectTaxi(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("internal static void ApplyTaxiReportDetails", StringComparison.Ordinal)];
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("if (taxi.EverApproved) taxiSignals = await taxiRequests.ListingTakenDownAsync") < At("taxi.Status = TaxiStatuses.Rejected"));
        Assert.True(At("CommitTransactionAsync") < At("taxiRequests.AfterCommitAsync(taxiSignals"));
    }
}

// Integration (WRITTEN, NOT RUN: needs RENTNEARBY_TEST_DB with PostGIS).
public class VehicleReviewJourneyIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationFact]
    public async Task Once_approved_taxi_under_review_is_selectable_but_cannot_ring()
    {
        OwnerSeed.OwnerRow driver;
        await using (var c = CreateContext())
        {
            driver = await OwnerSeed.TaxiDriverAsync(c);
            await c.Set<TaxiListing>().Where(t => t.Id == driver.ListingId)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, TaxiStatuses.PendingReview).SetProperty(t => t.EverApproved, true));
        }
        await using var db = CreateContext();
        Assert.True(await TaxiOfferRows.IsDriverSelectableAsync(db, driver.ListingId, CancellationToken.None));
        Assert.False(await TaxiOfferRows.IsDriverBookableAsync(db, driver.ListingId, CancellationToken.None));
        var row = await new EfDriverCalendarSql(db).LockDriverAsync(driver.ListingId, CancellationToken.None);
        Assert.True(row!.IsBookable);
    }

    [IntegrationFact]
    public async Task First_review_taxi_is_not_selectable()
    {
        OwnerSeed.OwnerRow driver;
        await using (var c = CreateContext()) driver = await OwnerSeed.TaxiDriverAsync(c, TaxiStatuses.PendingReview);
        await using var db = CreateContext();
        Assert.False(await TaxiOfferRows.IsDriverSelectableAsync(db, driver.ListingId, CancellationToken.None));
    }
}

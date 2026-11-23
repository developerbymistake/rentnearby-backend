using RentNearBy.Api.Handlers.TaxiRequests;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Owner.Be4;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Be4;

// Bug 25 (owner 2026-11-24): the driver home "Missed" number = his listing's Missed offers of the last 7 days.
public class TaxiHomeMissedTests
{
    [Fact]
    public void Missed_window_is_the_seven_day_answer_window()
    {
        Assert.Equal(7, TaxiDispatchRules.MissedWindowDays);
        Assert.Equal(AdminTaxiRequestRules.AnswerWindowDays, TaxiDispatchRules.MissedWindowDays);
    }

    [Fact]
    public void Missed_count_reads_his_listing_missed_offers_of_the_window_only()
    {
        var sql = TaxiDriverHomeReader.WorkSql;
        Assert.Contains("o.\"DriverListingId\" = @listing AND o.\"Status\" = 'Missed'", sql);
        Assert.Contains("o.\"OfferedAt\" >= now() - interval '7 days')::int AS \"MissedRequests\"", sql);
    }

    [Fact]
    public void Missed_requests_serializes_as_camel_case()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new OwnerTaxiNumbersDto(1, null, null, 0, 3),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"missedRequests\":3", json);
    }
}

// Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it.
public class TaxiHomeMissedIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static async Task OfferAsync(ApplicationDbContext c, Guid trip, Guid traveller, OwnerSeed.OwnerRow driver, string status, int daysAgo)
    {
        var requestId = Guid.NewGuid();
        c.Set<TaxiRequest>().Add(new TaxiRequest
        {
            Id = requestId, TripId = trip, TravellerUserId = traveller, Status = RequestStates.Expired, WantSightseeing = true,
            DispatchCityKey = 210000101, MatchToCityKey = 210000101, MatchFromCityKey = 210000101, Seats = 4, Travellers = 2,
            DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20)), DateTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(22)),
        });
        c.Set<TaxiRequestOffer>().Add(new TaxiRequestOffer
        {
            Id = Guid.NewGuid(), RequestId = requestId, DriverListingId = driver.ListingId, DriverUserId = driver.UserId, Status = status,
            SeqNo = 1, OfferedAt = DateTime.UtcNow.AddDays(-daysAgo), ExpiresAt = DateTime.UtcNow.AddDays(-daysAgo).AddMinutes(5),
        });
        await c.SaveChangesAsync();
    }

    [IntegrationFact]
    public async Task Home_counts_only_his_missed_offers_of_the_last_7_days()
    {
        OwnerSeed.OwnerRow me, other;
        await using (var c = CreateContext())
        {
            me = await OwnerSeed.TaxiDriverAsync(c);
            other = await OwnerSeed.TaxiDriverAsync(c);
            var traveller = await OwnerSeed.UserAsync(c, UserRoles.SeekerId, "Traveller");
            var trip = await BuddySeed.TripAsync(c, traveller);
            await OfferAsync(c, trip, traveller, me, OfferStates.Missed, 1);
            await OfferAsync(c, trip, traveller, me, OfferStates.Missed, 6);
            await OfferAsync(c, trip, traveller, me, OfferStates.Missed, 8);
            await OfferAsync(c, trip, traveller, me, OfferStates.Declined, 1);
            await OfferAsync(c, trip, traveller, me, OfferStates.NotSelected, 1);
            await OfferAsync(c, trip, traveller, other, OfferStates.Missed, 1);
        }

        await using var read = CreateContext();
        var clock = new Be4Clock(DateTime.UtcNow);
        var env = new OwnerHomeEnv(true, true);
        var mine = await new TaxiDriverHomeReader(read, new Be4Catalog(), new Be4Payment(true), clock).ReadAsync(me.UserId, env);
        var theirs = await new TaxiDriverHomeReader(read, new Be4Catalog(), new Be4Payment(true), clock).ReadAsync(other.UserId, env);

        Assert.Equal(2, mine!.Numbers.Taxi!.MissedRequests);
        Assert.Equal(1, theirs!.Numbers.Taxi!.MissedRequests);
    }

    [IntegrationFact]
    public async Task Home_missed_is_zero_when_none()
    {
        OwnerSeed.OwnerRow me;
        await using (var c = CreateContext()) me = await OwnerSeed.TaxiDriverAsync(c);
        await using var read = CreateContext();
        var home = await new TaxiDriverHomeReader(read, new Be4Catalog(), new Be4Payment(true), new Be4Clock(DateTime.UtcNow))
            .ReadAsync(me.UserId, new OwnerHomeEnv(true, true));
        Assert.Equal(0, home!.Numbers.Taxi!.MissedRequests);
    }
}

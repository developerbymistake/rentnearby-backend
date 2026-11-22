using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be4;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Owner.Be4;
using RentNearBy.Tests.Support.Buddy;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Journeys;

// JT-B-SK-2 (FB-02, FB-58): an owned trip gets status and next step; a joined viewer never sees the owner's enquiry data.
public class SeekerFeedJourneyTests
{
    private static readonly DateOnly Today = new(2026, 11, 22);

    private static Trip Trip(DateOnly? start, DateOnly? end) => new()
    {
        Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Title = "Hills", CityName = "Nainital", StartDate = start, EndDate = end,
        Status = TripStatuses.Planned, PlanJson = "{}",
    };

    [Fact]
    public void Viewer_card_has_role_viewer_and_no_owner_data()
    {
        var card = SeekerFeedReader.ViewerCard(Trip(Today.AddDays(5), Today.AddDays(7)), Today);
        Assert.Equal(TripSharingRoles.Viewer, card.Role);
        Assert.Null(card.NextStep);
        Assert.Equal(0, card.Done);
        Assert.Equal(0, card.Total);
        var json = JsonSerializer.Serialize(card, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("listingName", json);
        Assert.DoesNotContain("enquiryId", json);
    }

    [Fact]
    public void Reader_source_skips_enquiries_and_taxi_for_a_viewer()
    {
        var src = File.ReadAllText(Path.Combine(Trips.CompletionCounterGuardTests.Root(),
            "RentNearBy.Infrastructure", "Services", "Owner", "Be4", "SeekerFeedReader.cs"));
        var viewer = src.IndexOf("if (trip.OwnerUserId != userId)", StringComparison.Ordinal);
        var enquiries = src.IndexOf("db.TripEnquiries", StringComparison.Ordinal);
        Assert.True(viewer > 0 && enquiries > viewer);
        Assert.Contains(".Select(ForRows)", src);
        Assert.DoesNotContain("ListedStayBreakdownJson =", src);
        // The trip is chosen by its own statement, never from the capped slots list.
        Assert.DoesNotContain("slots.Trips", src);
    }
}

// Integration (WRITTEN, NOT RUN: needs RENTNEARBY_TEST_DB with PostGIS).
public class SeekerFeedJourneyIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static TimeProvider Clock => new Be4Clock(DateTime.UtcNow);

    [IntegrationFact]
    public async Task Joined_viewer_gets_two_statements_and_no_status()
    {
        Guid viewer;
        await using (var c = CreateContext())
        {
            var owner = await OwnerSeed.UserAsync(c, UserRoles.SeekerId, "Owner");
            viewer = await OwnerSeed.UserAsync(c, UserRoles.SeekerId, "Viewer");
            var trip = await BuddySeed.TripAsync(c, owner);
            await BuddySeed.MembersAsync(c, trip, owner, [viewer]);
        }
        await using var counting = Db.CreateCountingContext(out var counter);
        var feed = await new SeekerFeedReader(counting, Clock).ReadAsync(viewer);
        Assert.Equal(TripSharingRoles.Viewer, feed.TripCard.Role);
        Assert.Null(feed.Status);
        Assert.Null(feed.TripCard.NextStep);
        Assert.Equal(2, counter.Commands.Count);
    }

    [IntegrationFact]
    public async Task Owner_gets_four_statements_and_role_owner()
    {
        Guid owner;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.UserAsync(c, UserRoles.SeekerId, "Owner");
            await BuddySeed.TripAsync(c, owner);
        }
        await using var counting = Db.CreateCountingContext(out var counter);
        var feed = await new SeekerFeedReader(counting, Clock).ReadAsync(owner);
        Assert.Equal(TripSharingRoles.Owner, feed.TripCard.Role);
        Assert.Equal(4, counter.Commands.Count);
    }

    [IntegrationFact]
    public async Task Upcoming_trip_is_found_when_ended_trips_fill_the_slots_list()
    {
        Guid owner;
        Guid upcoming;
        await using (var c = CreateContext())
        {
            owner = await OwnerSeed.UserAsync(c, UserRoles.SeekerId, "Owner");
            var today = IstTime.TodayIst(DateTime.UtcNow);
            for (var i = 0; i < TripLimits.MaxTripsPerUser; i++) await BuddySeed.TripAsync(c, owner, 2, today.AddDays(-30 - i));
            upcoming = await BuddySeed.TripAsync(c, owner, 2, today.AddDays(20));
        }
        await using var read = CreateContext();
        var feed = await new SeekerFeedReader(read, Clock).ReadAsync(owner);
        Assert.Equal(upcoming, feed.TripCard.TripId);
    }
}

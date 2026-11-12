using RentNearBy.Api.Handlers;
using RentNearBy.Core.Models;

namespace RentNearBy.Tests.Trips;

public class JourneyGuardsTests
{
    private static readonly DateOnly Today = new(2026, 11, 12);
    private static (bool, string) Free => (true, JourneyLegStatuses.Free);
    private static (bool, string) Waiting => (true, JourneyLegStatuses.NoAnswer);
    private static (bool, string) Unasked => (false, JourneyLegStatuses.Unknown);

    [Fact]
    public void Complete_future_date_is_refused() =>
        Assert.Equal(JourneyGuardErrors.TravelDateNotReached, JourneyGuards.CompleteBlock(Today.AddDays(3), Today, false, [Free]));

    [Fact]
    public void Complete_not_asked_is_refused() =>
        Assert.Equal(JourneyGuardErrors.JourneyNotAsked, JourneyGuards.CompleteBlock(Today, Today, false, [Unasked, Unasked]));

    [Fact]
    public void Complete_with_a_car_without_a_fare_is_refused() =>
        Assert.Equal(JourneyGuardErrors.JourneyNotBooked, JourneyGuards.CompleteBlock(Today, Today, false, [Free, Waiting]));

    [Fact]
    public void Complete_trip_linked_is_refused_even_when_everything_is_settled() =>
        Assert.Equal(JourneyGuardErrors.JourneyInTrip, JourneyGuards.CompleteBlock(Today, Today, true, [Free]));

    [Fact]
    public void Complete_on_the_day_with_every_car_answered_is_allowed()
    {
        Assert.Null(JourneyGuards.CompleteBlock(Today, Today, false, [Free, Free]));
        Assert.Null(JourneyGuards.CompleteBlock(Today.AddDays(-2), Today, false, [Free]));
    }

    [Fact]
    public void Same_driver_on_neighbouring_legs_is_refused_but_distinct_or_distant_drivers_pass()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.True(JourneyGuards.SameDriverOnNeighbours([(1, a), (2, a)]));
        Assert.False(JourneyGuards.SameDriverOnNeighbours([(1, a), (2, b)]));
        Assert.False(JourneyGuards.SameDriverOnNeighbours([(1, a), (2, b), (3, a)]));
        Assert.True(JourneyGuards.ClashesWithNeighbour(2, a, [(1, a), (3, b)]));
        Assert.False(JourneyGuards.ClashesWithNeighbour(2, b, [(1, a), (3, a)]));
    }

    [Fact]
    public void Leg_keys_must_chain_inside_the_plan()
    {
        var plan = new List<string> { "chaldwani", "ckathgodam", "calmora" };
        var ok = new List<(int, string)> { (1, TaxiWaysKeys.LegKey("chaldwani", "ckathgodam")), (2, TaxiWaysKeys.LegKey("ckathgodam", "calmora")) };
        var broken = new List<(int, string)> { (1, TaxiWaysKeys.LegKey("chaldwani", "ckathgodam")), (2, TaxiWaysKeys.LegKey("cnainital", "calmora")) };
        var foreign = new List<(int, string)> { (1, TaxiWaysKeys.LegKey("cdelhi", "ckathgodam")), (2, TaxiWaysKeys.LegKey("ckathgodam", "calmora")) };
        Assert.True(JourneyGuards.LegKeysChain(ok, plan));
        Assert.False(JourneyGuards.LegKeysChain(broken, plan));
        Assert.False(JourneyGuards.LegKeysChain(foreign, plan));
    }

    [Fact]
    public void Open_cap_is_three_per_leg()
    {
        Assert.True(JourneyGuards.LegHasRoom(2, 0));
        Assert.False(JourneyGuards.LegHasRoom(2, 1));
        Assert.False(JourneyGuards.LegHasRoom(3, 0));
    }

    [Fact]
    public void Ask_date_must_be_the_journey_date() =>
        Assert.False(JourneyGuards.AskDateMatches(Today, Today.AddDays(1)));

    [Fact]
    public void Fare_on_an_expired_lead_is_refused_in_both_branches_and_a_live_one_passes()
    {
        var now = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        Assert.False(JourneyGuards.FareAllowed(TripEnquiryStatuses.FareSent, now.AddMinutes(-1), now));
        Assert.False(JourneyGuards.FareAllowed(TripEnquiryStatuses.Asked, now.AddMinutes(-1), now));
        Assert.True(JourneyGuards.FareAllowed(TripEnquiryStatuses.FareSent, now.AddHours(2), now));
        Assert.True(JourneyGuards.FareAllowed(TripEnquiryStatuses.Asked, now.AddHours(2), now));
        Assert.False(JourneyGuards.FareAllowed(TripEnquiryStatuses.Closed, now.AddHours(2), now));
    }
}

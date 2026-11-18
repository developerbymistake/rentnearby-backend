using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// The allowed edges are typed out here from MASTER_PLAN 2.3; every other ordered pair must be refused.
public class TaxiRequestTransitionsTests
{
    private static readonly string[] RequestAllowed =
    [
        "Open>Collecting", "Open>Cancelled", "Open>Expired",
        "Collecting>Selected", "Collecting>Open", "Collecting>Cancelled", "Collecting>Expired",
        "Selected>Booked", "Selected>Collecting", "Selected>Open", "Selected>Cancelled", "Selected>Expired",
        "Booked>InProgress", "Booked>Cancelled", "Booked>Open", "Booked>Completed",
        "InProgress>Completed", "InProgress>Cancelled",
    ];

    private static readonly string[] OfferAllowed =
    [
        "Ringing>Accepted", "Ringing>Declined", "Ringing>Missed", "Ringing>Closed",
        "Accepted>Selected", "Accepted>NotAgreed", "Accepted>NotSelected", "Accepted>Withdrawn", "Accepted>Closed",
        "Selected>Confirmed", "Selected>Declined", "Selected>Missed", "Selected>NotSelected", "Selected>Closed",
        "Confirmed>Withdrawn",
    ];

    private static readonly string[] BookingAllowed =
    [
        "Booked>InProgress", "Booked>Completed", "Booked>Cancelled",
        "InProgress>Completed", "InProgress>Cancelled",
    ];

    private static string[] AllowedFor(string kind) => kind switch
    {
        "Request" => RequestAllowed,
        "Offer" => OfferAllowed,
        _ => BookingAllowed,
    };

    public static IEnumerable<object[]> Kinds() => TransitionKinds.All.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_allowed_edge_passes_and_every_other_pair_fails(string kind)
    {
        var allowed = AllowedFor(kind).ToHashSet();
        var states = TaxiRequestTransitions.States(kind);
        var passed = 0;
        foreach (var from in states)
            foreach (var to in states)
            {
                var expected = allowed.Contains($"{from}>{to}");
                Assert.True(expected == TaxiRequestTransitions.CanMove(kind, from, to), $"{kind} {from}>{to} expected {expected}");
                if (expected) passed++;
            }
        Assert.Equal(allowed.Count, passed);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    [InlineData("Expired")]
    public void Final_request_states_move_nowhere(string from)
        => Assert.All(RequestStates.All, to => Assert.False(TaxiRequestTransitions.CanMove("Request", from, to)));

    [Fact]
    public void Offers_other_than_the_listed_ones_are_final()
    {
        foreach (var from in new[] { "Declined", "Missed", "NotSelected", "NotAgreed", "Withdrawn", "Closed" })
            Assert.All(OfferStates.All, to => Assert.False(TaxiRequestTransitions.CanMove("Offer", from, to)));
    }

    [Fact]
    public void A_state_never_moves_to_itself()
    {
        foreach (var kind in TransitionKinds.All)
            Assert.All(TaxiRequestTransitions.States(kind), s => Assert.False(TaxiRequestTransitions.CanMove(kind, s, s)));
    }

    [Fact]
    public void Unknown_states_and_kinds_are_refused()
    {
        Assert.False(TaxiRequestTransitions.CanMove("Request", "Nope", "Open"));
        Assert.False(TaxiRequestTransitions.CanMove("Request", "Open", "Nope"));
        Assert.Throws<ArgumentOutOfRangeException>(() => TaxiRequestTransitions.CanMove("Journey", "Open", "Booked"));
    }
}

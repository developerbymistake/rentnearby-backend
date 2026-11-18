using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// One case per row of the RequestView table in contract section 2 (plus the precedence inside Open).
public class TaxiRequestViewRulesTests
{
    private static readonly DateTime Wait = new(2026, 12, 10, 9, 51, 7, DateTimeKind.Utc);

    [Fact]
    public void Open_no_offer_ended_and_not_waiting_is_Finding()
        => Assert.Equal("Finding", TaxiRequestViewRules.Derive("Open", null, false, true, null, false));

    [Fact]
    public void Open_no_ringing_offer_and_wait_set_is_FindingWaiting()
        => Assert.Equal("FindingWaiting", TaxiRequestViewRules.Derive("Open", null, false, false, Wait, false));

    [Fact]
    public void Open_waiting_wins_over_a_missed_offer()
        => Assert.Equal("FindingWaiting", TaxiRequestViewRules.Derive("Open", null, true, false, Wait, false));

    [Fact]
    public void Open_with_a_ringing_offer_ignores_a_stale_wait()
        => Assert.Equal("Finding", TaxiRequestViewRules.Derive("Open", null, false, true, Wait, false));

    [Fact]
    public void Open_with_a_missed_or_declined_offer_is_NoReply()
        => Assert.Equal("NoReply", TaxiRequestViewRules.Derive("Open", null, true, true, null, false));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Collecting_is_always_Collecting(bool anyEnded)
        => Assert.Equal("Collecting", TaxiRequestViewRules.Derive("Collecting", null, anyEnded, anyEnded, anyEnded ? Wait : null, false));

    [Fact]
    public void Selected_is_Selected() => Assert.Equal("Selected", TaxiRequestViewRules.Derive("Selected", null, false, false, null, false));

    [Fact]
    public void Booked_is_Booked() => Assert.Equal("Booked", TaxiRequestViewRules.Derive("Booked", null, false, false, null, false));

    [Fact]
    public void InProgress_is_InProgress() => Assert.Equal("InProgress", TaxiRequestViewRules.Derive("InProgress", null, false, false, null, false));

    [Fact]
    public void Completed_not_rated_is_Rate() => Assert.Equal("Rate", TaxiRequestViewRules.Derive("Completed", null, false, false, null, false));

    [Fact]
    public void Completed_rated_is_Done() => Assert.Equal("Done", TaxiRequestViewRules.Derive("Completed", null, false, false, null, true));

    [Fact]
    public void Expired_AllDriversBusy_is_Busy() => Assert.Equal("Busy", TaxiRequestViewRules.Derive("Expired", "AllDriversBusy", true, false, null, false));

    [Fact]
    public void Expired_NotBooked_is_Ended() => Assert.Equal("Ended", TaxiRequestViewRules.Derive("Expired", "NotBooked", false, false, null, false));

    [Theory]
    [InlineData("TravellerCancelled")]
    [InlineData("PickupChanged")]
    [InlineData("BookingCancelledByTraveller")]
    [InlineData("DriverLeftTrip")]
    [InlineData("AdminCancelled")]
    [InlineData("AccountClosed")]
    public void Cancelled_is_Ended(string reason)
        => Assert.Equal("Ended", TaxiRequestViewRules.Derive("Cancelled", reason, false, false, null, false));

    [Fact]
    public void Every_state_maps_to_a_known_view()
    {
        foreach (var state in RequestStates.All)
            Assert.Contains(TaxiRequestViewRules.Derive(state, state == "Expired" ? "NotBooked" : null, false, false, null, false), RequestViews.All);
    }

    [Fact]
    public void Unknown_state_is_refused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => TaxiRequestViewRules.Derive("Weird", null, false, false, null, false));

    [Theory]
    [InlineData(null, "Optional", "Request taxi")]
    [InlineData("Busy", "Optional", "Request taxi")]
    [InlineData("Ended", "Optional", "Request taxi")]
    [InlineData("Brand new", "Optional", "Request taxi")]
    [InlineData("Finding", "Requested", "View request")]
    [InlineData("FindingWaiting", "Requested", "View request")]
    [InlineData("NoReply", "Requested", "View request")]
    [InlineData("Collecting", "Requested", "View request")]
    [InlineData("Selected", "Requested", "View request")]
    [InlineData("Booked", "Booked", "View booking")]
    [InlineData("InProgress", "Booked", "View booking")]
    [InlineData("Rate", "Completed", "View booking")]
    [InlineData("Done", "Completed", "View booking")]
    public void Overview_card_follows_the_view(string? view, string chip, string button)
        => Assert.Equal((chip, button), TaxiRequestViewRules.OverviewCard(view));

    [Theory]
    [InlineData(null, "todo")]
    [InlineData("Open", "asked")]
    [InlineData("Collecting", "offer")]
    [InlineData("Selected", "booked")]
    [InlineData("Booked", "confirmed")]
    [InlineData("InProgress", "confirmed")]
    [InlineData("Completed", "confirmed")]
    [InlineData("Cancelled", "todo")]
    [InlineData("Expired", "todo")]
    public void Make_it_easy_row_state(string? state, string expected)
        => Assert.Equal(expected, TaxiRequestViewRules.EasyRowState(state));
}

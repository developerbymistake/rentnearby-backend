using RentNearBy.Api.Handlers;

namespace RentNearBy.Tests.Trips;

public class TripOfferRulesTests
{
    private static TripOfferLine Line(string? room, int units, int guests, int price) =>
        new() { RoomName = room, Units = units, Guests = guests, PricePerNight = price };

    [Fact]
    public void Constants_match_the_contract()
    {
        Assert.Equal(3, TripOfferRules.MaxCounters);
        Assert.Equal(5, TripOfferRules.MaxRevised);
        Assert.Equal(100, TripOfferRules.CounterMinPerNight);
        Assert.Equal(9_900_000, TripOfferRules.CounterMaxPerNight);
        Assert.Equal(9_900_000, TripOfferRules.OfferMaxPerNight);
        Assert.Equal(StayLimits.MaxPrice, TripOfferRules.OfferMaxPerNight);
        Assert.Equal(300, TripOfferRules.ReasonMax);
    }

    [Fact]
    public void CounterTotal_saturates_instead_of_overflowing()
    {
        var total = TripOfferRules.CounterTotal(9_900_000, 15, 15);
        Assert.Equal(int.MaxValue, total);
        Assert.True(total > TripExecutionRules.MaxAmount);
    }

    [Theory]
    [InlineData(3600, 1, 1, 3600)]
    [InlineData(3600, 2, 3, 21600)]
    [InlineData(1900, 1, 2, 3800)]
    [InlineData(1000, 0, 0, 1000)]
    [InlineData(1000, -2, -1, 1000)]
    public void CounterTotal_is_nights_times_units_times_price(int price, int units, int nights, int expected)
        => Assert.Equal(expected, TripOfferRules.CounterTotal(price, units, nights));

    [Fact]
    public void ChangeLines_keeps_the_price_and_follows_the_rooms()
    {
        var lines = TripOfferRules.ChangeLines([Line("Family room", 1, 2, 1900)], rooms: 2, guests: 4);
        var l = Assert.Single(lines);
        Assert.Equal("Family room", l.RoomName);
        Assert.Equal(1900, l.PricePerNight);
        Assert.Equal(2, l.Units);
        Assert.Equal(4, l.Guests);
    }

    [Fact]
    public void ChangeLines_fewer_rooms_are_taken_from_the_last_line()
    {
        var accepted = new List<TripOfferLine> { Line("A", 1, 2, 1900), Line("B", 2, 4, 2500) };
        var lines = TripOfferRules.ChangeLines(accepted, rooms: 2, guests: 3);
        Assert.Equal(2, lines.Count);
        Assert.Equal(1, lines[0].Units);
        Assert.Equal(1, lines[1].Units);
        Assert.Equal(1900, lines[0].PricePerNight);
        Assert.Equal(2500, lines[1].PricePerNight);
        Assert.Equal(3, lines.Sum(x => x.Guests));
        Assert.All(lines, x => Assert.True(x.Guests >= 1));
    }

    [Fact]
    public void ChangeLines_drops_a_line_that_loses_all_its_rooms()
    {
        var accepted = new List<TripOfferLine> { Line("A", 1, 2, 1900), Line("B", 2, 4, 2500) };
        var lines = TripOfferRules.ChangeLines(accepted, rooms: 1, guests: 3);
        var l = Assert.Single(lines);
        Assert.Equal("A", l.RoomName);
        Assert.Equal(1, l.Units);
        Assert.Equal(3, l.Guests);
        Assert.Equal(1900, l.PricePerNight);
    }

    [Fact]
    public void ChangeLines_extra_rooms_go_to_the_first_line()
    {
        var accepted = new List<TripOfferLine> { Line("A", 1, 2, 1900), Line("B", 1, 2, 2500) };
        var lines = TripOfferRules.ChangeLines(accepted, rooms: 4, guests: 8);
        Assert.Equal(2, lines.Count);
        Assert.Equal(3, lines[0].Units);
        Assert.Equal(1, lines[1].Units);
        Assert.Equal(4, lines.Sum(x => x.Units));
        Assert.Equal(8, lines.Sum(x => x.Guests));
    }

    [Fact]
    public void ChangeLines_never_changes_a_per_night_price()
    {
        var accepted = new List<TripOfferLine> { Line("A", 2, 4, 1900), Line("B", 1, 2, 2500) };
        foreach (var rooms in new[] { 1, 2, 3, 5, 9 })
        {
            var lines = TripOfferRules.ChangeLines(accepted, rooms, guests: rooms * 2);
            Assert.All(lines, x => Assert.Contains(x.PricePerNight, new[] { 1900, 2500 }));
            Assert.Equal(rooms, lines.Sum(x => x.Units));
        }
    }

    [Fact]
    public void ChangeLines_clamps_rooms_and_guests_to_one()
    {
        var lines = TripOfferRules.ChangeLines([Line("A", 1, 2, 1900)], rooms: 0, guests: 0);
        var l = Assert.Single(lines);
        Assert.Equal(1, l.Units);
        Assert.Equal(1, l.Guests);
    }

    [Fact]
    public void ChangeLines_without_accepted_lines_returns_one_line()
    {
        var lines = TripOfferRules.ChangeLines([], rooms: 2, guests: 3);
        var l = Assert.Single(lines);
        Assert.Equal(2, l.Units);
        Assert.Equal(3, l.Guests);
    }

    [Fact]
    public void ChangeLines_total_scales_with_rooms_and_nights()
    {
        var lines = TripOfferRules.ChangeLines([Line("A", 1, 2, 1900)], rooms: 2, guests: 4);
        Assert.Equal(7600, TripExecutionRules.ComputeTotal(lines, 2));
    }

    [Theory]
    [InlineData(TripOfferKinds.Revised, "traveller", true)]
    [InlineData(TripOfferKinds.Revised, "owner", false)]
    [InlineData(TripOfferKinds.ChangeRequest, "owner", true)]
    [InlineData(TripOfferKinds.ChangeRequest, "traveller", false)]
    [InlineData(TripOfferKinds.Offer, "traveller", false)]
    [InlineData(TripOfferKinds.Offer, "owner", false)]
    [InlineData(TripOfferKinds.Counter, "traveller", false)]
    [InlineData(TripOfferKinds.Counter, "owner", false)]
    public void MovesSnapshot_matrix(string kind, string side, bool expected)
        => Assert.Equal(expected, TripOfferRules.MovesSnapshot(kind, side));

    [Fact]
    public void Side_names_match_the_auth_helper()
    {
        Assert.Equal("traveller", TripEnquiryAuth.Traveller);
        Assert.Equal("owner", TripEnquiryAuth.Owner);
    }

    [Fact]
    public void Report_type_and_card_status_constants()
    {
        Assert.Equal("TripEnquiry", ListingReportTypes.TripEnquiry);
        Assert.Equal("kept", TripCardStatuses.Kept);
        Assert.Equal("trip_counter", TripMessageTypes.Counter);
        Assert.Equal("trip_revised_offer", TripMessageTypes.RevisedOffer);
        Assert.Equal("trip_change_request", TripMessageTypes.ChangeRequest);
    }

    [Fact]
    public void New_error_types_equal_their_names()
    {
        Assert.Equal("CounterLimit", TripEnquiryErrorTypes.CounterLimit);
        Assert.Equal("CounterNotAllowed", TripEnquiryErrorTypes.CounterNotAllowed);
        Assert.Equal("RevisedNotAllowed", TripEnquiryErrorTypes.RevisedNotAllowed);
        Assert.Equal("ChangeNotAllowed", TripEnquiryErrorTypes.ChangeNotAllowed);
        Assert.Equal("ChangeNotPending", TripEnquiryErrorTypes.ChangeNotPending);
        Assert.Equal("NothingChanged", TripEnquiryErrorTypes.NothingChanged);
        Assert.Equal("TripNotRunning", TripEnquiryErrorTypes.TripNotRunning);
        Assert.Equal("TripNotEnded", TripEnquiryErrorTypes.TripNotEnded);
        Assert.Equal("ProblemOutsideTrip", TripEnquiryErrorTypes.ProblemOutsideTrip);
        Assert.Equal("AskAgainNothing", TripEnquiryErrorTypes.AskAgainNothing);
    }
}

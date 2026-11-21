using System.Text.Json;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Owner.Be2;

// T4 / T5 / AU-14a: the listed total written once when the traveller asks, its night segments (host only) and the traveller's
// listed per night while Asked.
public class ListedBreakdownTests
{
    private static readonly DateOnly Nov7 = new(2026, 11, 7);
    private static readonly List<ListedRoom> Rooms = [new("Deluxe", 3000, 2, 0), new("Family Room", 4500, 4, 1)];
    private static readonly LabelledRule Diwali = new(new PriceRule(new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 12), false, 20), "Diwali week");

    [Fact]
    public void Two_base_nights_and_one_festival_night_are_two_segments()
    {
        var quote = StayListedQuote.Compute([Rooms[0]], null, Nov7, 3, [Diwali])!;
        Assert.Equal(3000 + 3000 + 3600, quote.Total);
        Assert.Equal(
            [new ListedBreakdownLineDto("Deluxe", 1, 2, 3000, null), new ListedBreakdownLineDto("Deluxe", 1, 1, 3600, "Diwali week")],
            quote.Breakdown);
    }

    [Fact]
    public void No_rule_is_one_segment()
    {
        var quote = StayListedQuote.Compute([Rooms[0]], null, Nov7, 2, [])!;
        Assert.Equal(6000, quote.Total);
        Assert.Equal([new ListedBreakdownLineDto("Deluxe", 1, 2, 3000, null)], quote.Breakdown);
    }

    [Fact]
    public void Without_a_party_the_cheapest_room_one_unit_is_listed()
    {
        var quote = StayListedQuote.Compute(Rooms, null, Nov7, 1, [])!;
        Assert.Equal(3000, quote.Total);
        Assert.Equal("Deluxe", Assert.Single(quote.Breakdown).RoomName);
    }

    [Fact]
    public void A_party_uses_its_quote_lines_with_the_dated_prices()
    {
        var quote = StayListedQuote.Compute(Rooms, 6, Nov7, 3, [Diwali])!;
        var fit = StayPartyFit.Quote(Rooms.Select(r => new StayFitRoom(r.RoomName, r.MaxGuests, r.PricePerNight, r.Floor)).ToList(), 6, null, false, 3);
        var plain = new List<PriceRule> { Diwali.Rule };
        var expected = fit.Lines.Sum(l => l.Units * StaySpecialPriceMath.Total(l.PricePerNight!.Value, Nov7, 3, plain));
        Assert.Equal(expected, quote.Total);
        Assert.Equal(expected, quote.Breakdown.Sum(b => b.Units * b.Nights * b.PerNight));
    }

    [Fact]
    public void A_party_that_does_not_fit_falls_back_to_the_cheapest_room()
    {
        var quote = StayListedQuote.Compute([new ListedRoom("Deluxe", 3000, 2, 0)], 200, Nov7, 1, [])!;
        Assert.Equal(3000, quote.Total);
    }

    [Fact]
    public void No_priced_room_or_a_total_below_100_stores_nothing()
    {
        Assert.Null(StayListedQuote.Compute([new ListedRoom("Dorm", 0, 1, null)], null, Nov7, 2, []));
        Assert.Null(StayListedQuote.Compute([], null, Nov7, 2, []));
        Assert.Null(StayListedQuote.Compute([new ListedRoom("Dorm", 40, 1, null)], null, Nov7, 2, []));
    }

    [Fact]
    public void Listed_fields_follow_the_viewer_and_the_status()
    {
        var traveller = Guid.NewGuid();
        var host = Guid.NewGuid();
        var breakdown = JsonSerializer.Serialize(new List<ListedBreakdownLineDto> { new("Deluxe", 1, 3, 3200, "Diwali week") }, TripJson.Options);
        var e = new TripEnquiry
        {
            Id = Guid.NewGuid(), Kind = TripEnquiryKinds.Stay, TravellerUserId = traveller, OwnerUserId = host, ListingId = Guid.NewGuid(),
            ListingName = "Lakeview", CheckIn = Nov7, CheckOut = Nov7.AddDays(3), Status = TripEnquiryStatuses.Asked,
            ListedStayTotal = 9600, ListedStayBreakdownJson = breakdown, ContextJson = "{}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var none = new TripPartyInfo(null, null, null, false, null);

        var hostView = TripEnquiryDtoBuilder.Build(e, [], none, none, host, DateTime.UtcNow);
        Assert.Equal(9600, hostView.ListedStayTotal);
        Assert.Equal("Diwali week", Assert.Single(hostView.ListedBreakdown!).SpecialLabel);
        Assert.Null(hostView.ListedPerNight);

        var guestView = TripEnquiryDtoBuilder.Build(e, [], none, none, traveller, DateTime.UtcNow);
        Assert.Null(guestView.ListedStayTotal);
        Assert.Null(guestView.ListedBreakdown);
        Assert.Equal(3200, guestView.ListedPerNight);

        e.Status = TripEnquiryStatuses.Offered;
        Assert.Null(TripEnquiryDtoBuilder.Build(e, [], none, none, traveller, DateTime.UtcNow).ListedPerNight);
    }

    [Fact]
    public void An_old_row_without_a_snapshot_shows_nothing()
    {
        var traveller = Guid.NewGuid();
        var e = new TripEnquiry
        {
            Id = Guid.NewGuid(), Kind = TripEnquiryKinds.Stay, TravellerUserId = traveller, ListingId = Guid.NewGuid(), ListingName = "Old",
            CheckIn = Nov7, CheckOut = Nov7.AddDays(1), Status = TripEnquiryStatuses.Asked, ContextJson = "{}",
        };
        Assert.Null(TripEnquiryDtoBuilder.ListedPerNightFor(e, travellerViewer: true));
        Assert.Null(TripEnquiryDtoBuilder.ReadBreakdown("broken"));
    }
}

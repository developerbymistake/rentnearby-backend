using RentNearBy.Api.Handlers;
using RentNearBy.Core.Entities;

namespace RentNearBy.Tests.Trips;

// Journey integrity, owner D: host-side gates (pure parts; the DB paths skip without PostgreSQL).
public class TripHostGuardsTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 12);

    private static TripOffer CounterOffer(int total, DateOnly checkIn, DateOnly checkOut, params TripOfferLine[] lines) => new()
    {
        Kind = TripOfferKinds.Counter, Total = total, CheckIn = checkIn, CheckOut = checkOut,
        LinesJson = System.Text.Json.JsonSerializer.Serialize(lines, TripJson.Options),
    };

    private static TripOfferLine Line(string room, int units, int guests, int price) =>
        new() { RoomName = room, Units = units, Guests = guests, PricePerNight = price };

    // ----- m10: an over enquiry is closed for the host even before the timer sweeps it -----

    [Theory]
    [InlineData(TripEnquiryStatuses.Asked, -1, 5, true)]
    [InlineData(TripEnquiryStatuses.Countered, -1, 5, true)]
    [InlineData(TripEnquiryStatuses.Offered, -1, 5, true)]
    [InlineData(TripEnquiryStatuses.Countered, 3, 5, false)]
    [InlineData(TripEnquiryStatuses.Asked, 3, -1, true)]
    [InlineData(TripEnquiryStatuses.Asked, 3, 5, false)]
    public void Enquiry_over_by_window_or_past_check_in(string status, int expiresInHours, int checkInDays, bool over)
    {
        var checkIn = Today.AddDays(checkInDays);
        Assert.Equal(over, TripHostGuards.EnquiryOver(status, Now.AddHours(expiresInHours), checkIn, Now, Today));
    }

    // ----- m9: a counter is accepted only by an equal offer -----

    [Fact]
    public void Equal_offer_accepts_the_counter()
    {
        var counter = CounterOffer(4000, Today.AddDays(3), Today.AddDays(5), Line("Deluxe", 1, 2, 2000));
        Assert.True(TripHostGuards.CounterMatches(counter, Today.AddDays(3), Today.AddDays(5), 4000, [Line("deluxe", 1, 2, 2000)]));
    }

    [Theory]
    [InlineData(4500, 3, 5, 2250)]
    [InlineData(4000, 3, 6, 2000)]
    [InlineData(4000, 4, 5, 2000)]
    [InlineData(4000, 3, 5, 1500)]
    public void Different_offer_does_not_accept_the_counter(int total, int inDay, int outDay, int price)
    {
        var counter = CounterOffer(4000, Today.AddDays(3), Today.AddDays(5), Line("Deluxe", 1, 2, 2000));
        Assert.False(TripHostGuards.CounterMatches(counter, Today.AddDays(inDay), Today.AddDays(outDay), total, [Line("Deluxe", 1, 2, price)]));
    }

    // ----- m12: advance within the accepted offer (policy) -----

    [Theory]
    [InlineData(5000, 1000, 1000, true)]
    [InlineData(5000, 1000, 1001, false)]
    [InlineData(5000, null, 5000, true)]
    [InlineData(5000, null, 5001, false)]
    [InlineData(5000, 0, 5000, true)]
    [InlineData(null, 1000, 1000, true)]
    public void Advance_stays_within_the_accepted_offer(int? agreedTotal, int? offerAdvance, int asked, bool ok)
        => Assert.Equal(ok, asked <= TripHostGuards.MaxAdvance(agreedTotal, offerAdvance));

    // ----- m14: the offer must seat the party -----

    [Fact]
    public void Offer_that_seats_the_party_fits()
        => Assert.True(TripHostGuards.PartyFits([Line("Deluxe", 2, 4, 2000)], 4, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Deluxe"] = 2 }));

    [Fact]
    public void Offer_for_fewer_guests_than_the_party_does_not_fit()
        => Assert.False(TripHostGuards.PartyFits([Line("Deluxe", 1, 2, 2000)], 5, null));

    [Fact]
    public void Offer_whose_rooms_hold_fewer_than_the_party_does_not_fit()
        => Assert.False(TripHostGuards.PartyFits([Line("Deluxe", 1, 6, 2000)], 6, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Deluxe"] = 2 }));

    // ----- offer inside the enquiry's leg -----

    [Fact]
    public void Offer_nights_must_sit_inside_the_leg_of_the_enquiry()
    {
        var plan = new TripPlan
        {
            Legs = [new TripCityLeg { Index = 1, CityName = "A" }, new TripCityLeg { Index = 2, CityName = "B" }],
            Days = [new TripDay { Day = 1, Leg = 1 }, new TripDay { Day = 2, Leg = 1 }, new TripDay { Day = 3, Leg = 2 }, new TripDay { Day = 4, Leg = 2 }],
        };
        var start = new DateOnly(2026, 12, 1);
        Assert.True(TripHostGuards.OfferInsideLeg(plan, start, 1, start, start.AddDays(2)));
        Assert.False(TripHostGuards.OfferInsideLeg(plan, start, 1, start.AddDays(1), start.AddDays(3)));
        Assert.True(TripHostGuards.OfferInsideLeg(plan, start, 3, start.AddDays(2), start.AddDays(4)));
        Assert.True(TripHostGuards.OfferInsideLeg(null, start, 1, start, start.AddDays(9)));
    }

    // ----- B7: take-down closes only the unconfirmed rows -----

    [Theory]
    [InlineData(TripEnquiryStatuses.Asked, true)]
    [InlineData(TripEnquiryStatuses.Offered, true)]
    [InlineData(TripEnquiryStatuses.Countered, true)]
    [InlineData(TripEnquiryStatuses.FareSent, true)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, false)]
    [InlineData(TripEnquiryStatuses.BookedByTraveller, false)]
    [InlineData(TripEnquiryStatuses.NotAvailable, false)]
    public void Take_down_closes_open_unconfirmed_rows_and_keeps_booked_ones(string status, bool closes)
        => Assert.Equal(closes, TripHostGuards.ClosedByTakeDown(status));
}

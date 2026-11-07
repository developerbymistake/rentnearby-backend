using System.Text.Json;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Trips;

public class TripRound3PushFixTests
{
    private static readonly Guid Traveller = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000011");
    private static readonly Guid Owner = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000012");
    private static readonly DateOnly Start = new(2026, 11, 5);
    private static readonly DateTime Now = new(2026, 11, 6, 6, 0, 0, DateTimeKind.Utc);

    private static TripEnquiry Stay(DateOnly checkIn, DateOnly checkOut, string status = TripEnquiryStatuses.ConfirmedByBoth) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        TravellerUserId = Traveller,
        OwnerUserId = Owner,
        Kind = TripEnquiryKinds.Stay,
        ListingId = Guid.NewGuid(),
        ListingName = "Deodar Homestay",
        CheckIn = checkIn,
        CheckOut = checkOut,
        Status = status,
        AgreedTotal = 3000,
        ContextJson = """{"city":"Mussoorie","adults":2,"kids":0}""",
    };

    private static TripPushContext Ctx(Guid recipient, int? total = null) => new(recipient, "Ravi Kumar", "Deodar Homestay", null, total);

    // Finding 1: offer expiry reaches both sides on existing types.
    [Fact]
    public void Offer_expiry_pushes_traveller_and_host()
    {
        var e = Stay(Start, Start.AddDays(1), TripEnquiryStatuses.Asked);
        var t = TripNotifications.TripOfferExpired(e, Ctx(Traveller));
        Assert.Equal(Traveller, t.TargetUserId);
        Assert.Equal(NotificationTypes.TripEnquiryExpired, t.Type);
        Assert.Equal("Deodar Homestay offer expired", t.Title);

        var o = TripNotifications.TripOfferLapsed(e, Ctx(Owner));
        Assert.Equal(Owner, o.TargetUserId);
        Assert.Equal(NotificationTypes.TripOfferDeclined, o.Type);
        Assert.Equal("Offer expired", o.Title);
        Assert.Contains("Send a new offer", o.Body);
    }

    [Fact]
    public void Offer_expiry_on_a_closed_window_does_not_ask_for_a_new_offer()
    {
        var e = Stay(Start, Start.AddDays(1), TripEnquiryStatuses.Expired);
        Assert.DoesNotContain("Send a new offer", TripNotifications.TripOfferLapsed(e, Ctx(Owner)).Body);
    }

    // Finding 2: the change request push shows the requested dates, people and total.
    [Fact]
    public void Change_request_shows_the_requested_values()
    {
        var e = Stay(Start, Start.AddDays(1));
        var ctx = Ctx(Owner, 8000) with { CheckIn = Start.AddDays(1), CheckOut = Start.AddDays(3) };
        var n = TripNotifications.TripChangeRequest(e, ctx, 3, 1);
        Assert.Contains("2 nights from 06 Nov", n.Body);
        Assert.Contains("3 adults, 1 kid", n.Body);
        Assert.Contains("Rs 8,000", n.Body);
        Assert.DoesNotContain("Night of 05 Nov", n.Body);
    }

    // Findings 3 and 26: a driver who lost days is not told he has a new lead.
    [Fact]
    public void Lead_days_taken_names_the_removed_and_kept_days()
    {
        var e = new TripEnquiry
        {
            Id = Guid.NewGuid(), TripId = Guid.NewGuid(), TravellerUserId = Traveller, OwnerUserId = Owner,
            Kind = TripEnquiryKinds.Taxi, ListingName = "Bisht Cabs", DaysJson = "[2]",
            CheckIn = Start.AddDays(1), CheckOut = Start.AddDays(2), Status = TripEnquiryStatuses.Asked,
        };
        var n = TripNotifications.TripLeadDaysTaken(e, Ctx(Owner), "05 Nov", quoteAgain: true);
        Assert.Equal("Ravi booked another taxi for 05 Nov", n.Title);
        Assert.Equal("Still asking for 06 Nov. Send your fare again.", n.Body);
        Assert.DoesNotContain("New trip lead", n.Title);
        Assert.Equal(UserNotificationRoutes.TripLeads, n.ActionRoute);

        var t = TripNotifications.TripFareCleared(e, Ctx(Traveller));
        Assert.Equal(Traveller, t.TargetUserId);
        using var doc = JsonDocument.Parse(t.ActionArgumentsJson!);
        Assert.Equal("taxi", doc.RootElement.GetProperty("row").GetString());
    }

    // Finding 10: a counter's proposed price is the guest's per-room price, not the first rescaled line.
    [Fact]
    public void Counter_proposed_per_night_is_the_guest_price()
    {
        var offered = new List<TripOfferLine>
        {
            new() { RoomName = "Deluxe", Units = 1, Guests = 2, PricePerNight = 3000 },
            new() { RoomName = "Standard", Units = 1, Guests = 2, PricePerNight = 2000 },
        };
        var lines = TripOfferRules.CounterLines(offered, 2000, 2, 4);
        var counter = new TripOffer { Id = Guid.NewGuid(), Kind = TripOfferKinds.Counter, LinesJson = JsonSerializer.Serialize(lines, TripJson.Options) };
        Assert.Equal(2400, lines[0].PricePerNight);
        Assert.Equal(2000, TripEnquiryDtoBuilder.ToOfferDto(counter, Stay(Start, Start.AddDays(1))).ProposedPerNight);

        var offer = new TripOffer { Id = Guid.NewGuid(), Kind = TripOfferKinds.Offer, LinesJson = JsonSerializer.Serialize(offered, TripJson.Options) };
        Assert.Null(TripEnquiryDtoBuilder.ToOfferDto(offer, Stay(Start, Start.AddDays(1))).ProposedPerNight);
    }

    [Fact]
    public void Phone_nights_cap_at_the_last_night()
    {
        Assert.Equal(Start.AddDays(2), TripDateRules.StayCheckOut(Start, 3, 1, allNights: true, nights: 2));
        Assert.Equal(Start.AddDays(3), TripDateRules.StayCheckOut(Start, 3, 1, allNights: false, nights: 9));
    }

    // Finding 20: both rows of a D2 split carry their own days and text.
    [Fact]
    public void Problem_split_rebuilds_both_contexts()
    {
        var context = new TripEnquiryContext
        {
            City = "Mussoorie", Adults = 2, Kids = 0,
            Days =
            [
                new() { Day = 1, Date = Start, Spots = ["Mall Road"] },
                new() { Day = 2, Date = Start.AddDays(1), Spots = ["Kempty Falls"] },
                new() { Day = 3, Date = Start.AddDays(2), Spots = ["Lal Tibba"] },
            ],
        };
        var e = Stay(Start, Start.AddDays(3));
        e.ContextJson = JsonSerializer.Serialize(context, TripJson.Options);

        var (kept, failed) = TripProblemSplit.Plan(e, Start, 2, Now);

        var k = JsonSerializer.Deserialize<TripEnquiryContext>(kept!.ContextJson!, TripJson.Options)!;
        var f = JsonSerializer.Deserialize<TripEnquiryContext>(failed.ContextJson!, TripJson.Options)!;
        Assert.Equal(new[] { 1 }, k.Days.Select(d => d.Day).ToArray());
        Assert.Equal(new[] { 2, 3 }, f.Days.Select(d => d.Day).ToArray());
        Assert.Contains("05 Nov to 06 Nov", k.Text);
        Assert.DoesNotContain("3 nights", k.Text);
        Assert.Contains("06 Nov to 08 Nov (2 nights)", f.Text);
    }

    // Finding 21: taxi rows keep their dates (D7) whatever the trip start is now.
    [Fact]
    public void Taxi_dates_follow_the_row_check_in()
    {
        var e = new TripEnquiry { Kind = TripEnquiryKinds.Taxi, DaysJson = "[1,2]", CheckIn = Start, CheckOut = Start.AddDays(2) };
        Assert.Equal(new[] { Start, Start.AddDays(1) }, TripEnquiryHandlers.TaxiDates(e).ToArray());

        var gaps = new TripEnquiry { Kind = TripEnquiryKinds.Taxi, DaysJson = "[3,1]", CheckIn = Start, CheckOut = Start.AddDays(3) };
        Assert.Equal(new[] { Start, Start.AddDays(2) }, TripEnquiryHandlers.TaxiDates(gaps).ToArray());

        var noDays = new TripEnquiry { Kind = TripEnquiryKinds.Taxi, CheckIn = Start, CheckOut = Start.AddDays(2) };
        Assert.Equal(new[] { Start, Start.AddDays(1) }, TripEnquiryHandlers.TaxiDates(noDays).ToArray());
    }
}

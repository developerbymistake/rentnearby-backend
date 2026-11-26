using System.Text.Json;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.Owner.Be1;

// AU-10, R-1: the Auto row of each source type, field by field; dates only, never a time or a fare; cancel keeps money notes.
public class CalendarAutoTextTests
{
    private static readonly DateOnly D = new(2026, 12, 20);

    private static EnquiryFacts Stay(string status = TripEnquiryStatuses.ConfirmedByBoth) => new()
    {
        Id = Guid.NewGuid(), Kind = TripEnquiryKinds.Stay, Status = status, TripId = Guid.NewGuid(), ListingId = Guid.NewGuid(),
        OwnerUserId = Guid.NewGuid(), TravellerUserId = Guid.NewGuid(), CheckIn = D, CheckOut = D.AddDays(3),
        ContextJson = """{"adults":2,"kids":1,"travellerName":"Neha J"}""", AgreedTotal = 12600, ListedStayTotal = 13500,
        OfferLinesJson = """[{"roomName":"Family Room","units":1,"guests":2,"pricePerNight":4200}]""", TravellerName = "Neha Joshi",
        UpdatedAt = new DateTime(2026, 12, 1, 20, 0, 0, DateTimeKind.Utc),
    };

    private static EnquiryFacts Lead(string status = TripEnquiryStatuses.ConfirmedByBoth) => new()
    {
        Id = Guid.NewGuid(), Kind = TripEnquiryKinds.Taxi, Status = status, TripId = Guid.NewGuid(), Day = null, ListingId = Guid.NewGuid(),
        OwnerUserId = Guid.NewGuid(), TravellerUserId = Guid.NewGuid(), CheckIn = D, CheckOut = D.AddDays(2), PickupPlace = "Kathgodam hub",
        ContextJson = """{"adults":3,"kids":0,"city":"Nainital"}""", TravellerName = "Seema", UpdatedAt = DateTime.UtcNow,
    };

    private static BookingFacts Booking(string status = BookingStates.Booked) => new()
    {
        Id = Guid.NewGuid(), Status = status, DriverListingId = Guid.NewGuid(), DriverUserId = Guid.NewGuid(), TravellerUserId = Guid.NewGuid(),
        DateFrom = D, DateTo = D.AddDays(4), PickupCityName = "Kathgodam", Seats = 4, TravellerName = "Seema Rawat", DropCityName = "Haldwani",
        DropSameAsPickup = false, Travellers = 3, BookedAt = new DateTime(2026, 12, 1, 4, 0, 0, DateTimeKind.Utc),
        SightseeingDaysJson = "[2,3,4,5,6]",
        TripSnapshotJson = JsonSerializer.Serialize(new TaxiTripSnapshot("Hills", new TaxiSnapshotPickup("Kathgodam hub", D), null, [],
        [
            new TaxiSnapshotDay(1, D, "Kathgodam", true, 0, 0, []),
            new TaxiSnapshotDay(2, D.AddDays(1), "Nainital", true, 0, 0, []),
            new TaxiSnapshotDay(3, D.AddDays(2), "Ranikhet", true, 0, 0, []),
            new TaxiSnapshotDay(4, D.AddDays(3), "Kausani", true, 0, 0, []),
            new TaxiSnapshotDay(5, D.AddDays(4), "Ranikhet", true, 0, 0, []),
            new TaxiSnapshotDay(6, D.AddDays(5), "Almora", true, 0, 0, []),
        ]), TaxiTripSnapshot.JsonOptions),
    };

    [Fact]
    public void Confirmed_stay_writes_dates_rooms_guests_and_both_totals()
    {
        var plan = CalendarAutoText.ForEnquiry(Stay())!;
        Assert.True(plan.Confirmed);
        Assert.Equal(CalendarSourceTypes.StayEnquiry, plan.SourceType);
        Assert.Equal(ListingKinds.Stay, plan.ListingKind);
        Assert.Equal((D, D.AddDays(3)), (plan.DateFrom, plan.DateTo));
        Assert.Equal("Neha Joshi", plan.Label);
        Assert.Equal("Family Room", plan.RoomNames);
        Assert.Equal((short)2, plan.Guests);
        Assert.Null(plan.Seats);
        Assert.Equal("Family Room · 2 guests", plan.Detail);
        Assert.Equal((13500, 12600), (plan.ListedTotal, plan.AgreedTotal));
        Assert.Null(plan.CancelledBy);
        Assert.Null(plan.PlacesJson);
    }

    [Fact]
    public void Stay_without_offer_lines_takes_the_party_from_the_context()
    {
        var facts = Stay();
        facts.OfferLinesJson = null;
        var plan = CalendarAutoText.ForEnquiry(facts)!;
        Assert.Equal((short)3, plan.Guests);
        Assert.Equal("3 guests", plan.Detail);
        Assert.Null(plan.RoomNames);
    }

    [Fact]
    public void Traveller_name_falls_back_to_the_context_then_blank()
    {
        var facts = Stay();
        facts.TravellerName = null;
        Assert.Equal("Neha J", CalendarAutoText.ForEnquiry(facts)!.Label);
        facts.ContextJson = "{}";
        Assert.Equal(string.Empty, CalendarAutoText.ForEnquiry(facts)!.Label);
    }

    [Theory]
    [InlineData(TripEnquiryStatuses.CancelledByTraveller, CalendarCancelledBy.Traveller)]
    [InlineData(TripEnquiryStatuses.CancelledByOwner, CalendarCancelledBy.Owner)]
    [InlineData(TripEnquiryStatuses.Failed, CalendarCancelledBy.System)]
    [InlineData(TripEnquiryStatuses.Expired, CalendarCancelledBy.System)]
    public void Stay_cancel_names_who_cancelled_and_the_ist_day(string status, string by)
    {
        var plan = CalendarAutoText.ForEnquiry(Stay(status))!;
        Assert.False(plan.Confirmed);
        Assert.Equal(by, plan.CancelledBy);
        Assert.Equal(new DateOnly(2026, 12, 2), plan.CancelledOn);   // 20:00 UTC is the next IST day
    }

    [Fact]
    public void Admin_close_is_admin_and_a_lead_cancel_by_the_host_is_the_driver()
    {
        var closed = Stay(TripEnquiryStatuses.Closed);
        closed.FailReason = TripCloseReasons.AdminClosed;
        Assert.Equal(CalendarCancelledBy.Admin, CalendarAutoText.ForEnquiry(closed)!.CancelledBy);
        Assert.Equal(CalendarCancelledBy.Driver, CalendarAutoText.ForEnquiry(Lead(TripEnquiryStatuses.CancelledByOwner))!.CancelledBy);
        var byTraveller = Stay(TripEnquiryStatuses.Closed);
        byTraveller.CancelledByUserId = byTraveller.TravellerUserId;
        Assert.Equal(CalendarCancelledBy.Traveller, CalendarAutoText.ForEnquiry(byTraveller)!.CancelledBy);
    }

    [Fact]
    public void Booked_by_traveller_lead_is_a_live_row_but_a_stay_is_not()
    {
        var lead = Lead(TripEnquiryStatuses.BookedByTraveller);
        var plan = CalendarAutoText.ForEnquiry(lead)!;
        Assert.True(plan.Confirmed);
        Assert.Equal(CalendarSourceTypes.TaxiLead, plan.SourceType);
        Assert.Equal((D, D.AddDays(1)), (plan.DateFrom, plan.DateTo));
        Assert.Null(plan.CancelledBy);
        Assert.Null(CalendarAutoText.CancelledByOfEnquiry(lead, CalendarSourceTypes.TaxiLead));
        Assert.False(CalendarAutoText.IsConfirmed(TripEnquiryStatuses.BookedByTraveller, CalendarSourceTypes.StayEnquiry));
        Assert.False(CalendarAutoText.IsConfirmed(TripEnquiryStatuses.FareSent, CalendarSourceTypes.TaxiLead));
    }

    [Fact]
    public void Confirmed_lead_covers_first_to_last_day_with_place_and_travellers()
    {
        var plan = CalendarAutoText.ForEnquiry(Lead())!;
        Assert.Equal(CalendarSourceTypes.TaxiLead, plan.SourceType);
        Assert.Equal(ListingKinds.Taxi, plan.ListingKind);
        Assert.Equal((D, D.AddDays(1)), (plan.DateFrom, plan.DateTo));
        Assert.Equal("Kathgodam hub", plan.FromPlace);
        Assert.Equal("Kathgodam hub · 3 travellers", plan.Detail);
        Assert.Equal((short)3, plan.Guests);
        Assert.Null(plan.AgreedTotal);
    }

    [Fact]
    public void Lead_without_check_in_counts_from_the_trip_start_and_is_skipped_without_one()
    {
        Assert.Equal((D.AddDays(1), D.AddDays(2)), CalendarAutoText.LeadRange(null, null, D, "[2,3]"));
        Assert.Null(CalendarAutoText.LeadRange(null, null, null, "[2,3]"));
        Assert.Equal((D, D), CalendarAutoText.LeadRange(D, null, null, null));
    }

    [Fact]
    public void Journey_cars_and_unknown_kinds_are_not_calendar_sources()
    {
        var car = Lead();
        car.Day = 2;
        Assert.Null(CalendarAutoText.ForEnquiry(car));
        var tripless = Lead();
        tripless.TripId = null;
        Assert.Null(CalendarAutoText.ForEnquiry(tripless));
    }

    [Fact]
    public void Confirmed_stay_without_dates_writes_nothing_a_cancelled_one_still_cancels()
    {
        var facts = Stay();
        facts.CheckOut = facts.CheckIn;
        Assert.Null(CalendarAutoText.ForEnquiry(facts));
        var cancelled = Stay(TripEnquiryStatuses.CancelledByTraveller);
        cancelled.CheckOut = null;
        Assert.False(CalendarAutoText.ForEnquiry(cancelled)!.Confirmed);
    }

    [Fact]
    public void Booking_writes_pickup_place_drop_city_seats_travellers_and_places()
    {
        var plan = CalendarAutoText.ForBooking(Booking());
        Assert.True(plan.Confirmed);
        Assert.Equal(CalendarSourceTypes.TaxiBooking, plan.SourceType);
        Assert.Equal((D, D.AddDays(4)), (plan.DateFrom, plan.DateTo));
        Assert.Equal("Kathgodam hub", plan.FromPlace);
        Assert.Equal("Haldwani", plan.ToPlace);
        Assert.Equal("Seema Rawat", plan.Label);
        Assert.Equal("4 seats", plan.Detail);
        Assert.Equal((short)4, plan.Seats);
        Assert.Equal((short)3, plan.Guests);
        Assert.Equal(["Nainital", "Ranikhet", "Kausani", "Almora"], JsonSerializer.Deserialize<List<string>>(plan.PlacesJson!));
        Assert.Null(plan.ListedTotal);
        Assert.Null(plan.AgreedTotal);
    }

    [Theory]
    [InlineData(BookingStates.InProgress, true)]
    [InlineData(BookingStates.Completed, true)]
    [InlineData(BookingStates.Cancelled, false)]
    public void Booking_states_map_to_present_or_cancelled(string status, bool confirmed)
        => Assert.Equal(confirmed, CalendarAutoText.ForBooking(Booking(status)).Confirmed);

    // D = 2026-12-20, DateTo = D + 4. CompletedAt is UTC; the end is its IST day.
    [Theory]
    [InlineData(2026, 12, 21, 20, 0, 2026, 12, 22)]
    [InlineData(2026, 12, 20, 10, 0, 2026, 12, 20)]
    [InlineData(2026, 12, 19, 10, 0, 2026, 12, 20)]
    [InlineData(2026, 12, 24, 18, 35, 2026, 12, 24)]
    public void Completed_booking_ends_on_the_ist_day_it_ended_never_before_its_first_day_never_after_its_last(
        int y, int mo, int d, int h, int mi, int ey, int em, int ed)
    {
        var facts = Booking(BookingStates.Completed);
        facts.CompletedAt = new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc);
        var plan = CalendarAutoText.ForBooking(facts);
        Assert.True(plan.Confirmed);
        Assert.Equal((D, new DateOnly(ey, em, ed)), (plan.DateFrom, plan.DateTo));
    }

    [Fact]
    public void Live_booking_and_completed_booking_without_a_time_keep_the_whole_range()
    {
        var live = Booking(BookingStates.InProgress);
        live.CompletedAt = new DateTime(2026, 12, 21, 4, 0, 0, DateTimeKind.Utc);
        Assert.Equal(D.AddDays(4), CalendarAutoText.ForBooking(live).DateTo);
        Assert.Equal(D.AddDays(4), CalendarAutoText.ForBooking(Booking(BookingStates.Completed)).DateTo);
    }

    [Theory]
    [InlineData(BookingCancelledBy.Traveller, CalendarCancelledBy.Traveller)]
    [InlineData(BookingCancelledBy.Driver, CalendarCancelledBy.Driver)]
    [InlineData(BookingCancelledBy.Admin, CalendarCancelledBy.Admin)]
    [InlineData(BookingCancelledBy.System, CalendarCancelledBy.System)]
    [InlineData(null, CalendarCancelledBy.System)]
    public void Booking_cancel_names_who_cancelled(string? by, string expected)
    {
        var facts = Booking(BookingStates.Cancelled);
        facts.CancelledBy = by;
        facts.CancelledAt = new DateTime(2026, 12, 27, 19, 0, 0, DateTimeKind.Utc);
        var plan = CalendarAutoText.ForBooking(facts);
        Assert.Equal(expected, plan.CancelledBy);
        Assert.Equal(new DateOnly(2026, 12, 28), plan.CancelledOn);
    }

    [Fact]
    public void Drop_same_as_pickup_uses_the_pickup_city_no_drop_is_null()
    {
        var same = Booking();
        same.DropSameAsPickup = true;
        same.DropCityName = null;
        Assert.Equal("Kathgodam", CalendarAutoText.ForBooking(same).ToPlace);
        var none = Booking();
        none.DropCityName = null;
        Assert.Null(CalendarAutoText.ForBooking(none).ToPlace);
        var noSnapshot = Booking();
        noSnapshot.TripSnapshotJson = null;
        Assert.Equal("Kathgodam", CalendarAutoText.ForBooking(noSnapshot).FromPlace);
        Assert.Null(CalendarAutoText.ForBooking(noSnapshot).PlacesJson);
    }

    [Fact]
    public void Texts_are_cut_to_the_column_lengths()
    {
        var facts = Stay();
        facts.TravellerName = new string('n', 90);
        facts.OfferLinesJson = JsonSerializer.Serialize(Enumerable.Range(0, 12).Select(i => new { roomName = $"Very Long Room Name {i}", guests = 1 }));
        var plan = CalendarAutoText.ForEnquiry(facts)!;
        Assert.Equal(OwnerLimits.LabelMax, plan.Label.Length);
        Assert.True(plan.RoomNames!.Length <= OwnerLimits.RoomNamesMax);
        Assert.True(plan.Detail!.Length <= OwnerLimits.DetailMax);
    }

    [Fact]
    public void Bad_json_never_throws()
    {
        var facts = Stay();
        facts.OfferLinesJson = "{not json";
        facts.ContextJson = "[";
        Assert.NotNull(CalendarAutoText.ForEnquiry(facts));
        var booking = Booking();
        booking.SightseeingDaysJson = "oops";
        Assert.Null(CalendarAutoText.ForBooking(booking).PlacesJson);
    }
}

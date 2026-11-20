using System.Text.Json;
using RentNearBy.Api.Handlers.TaxiRequests;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// Contract section 6: driver DTOs built from in-memory rows equal the shared samples.
public class TaxiDriverViewsTests
{
    internal static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly Guid RequestA = Guid.Parse("3f9a6c2d-1b7e-4f0a-8c55-2e7d9b4a6101");
    private static readonly Guid OfferA = Guid.Parse("a1b2c3d4-0001-4a00-8000-000000000001");
    private static readonly Guid Traveller = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 12, 10, 9, 43, 0, DateTimeKind.Utc);

    internal static void AssertEqualsSample(string sample, object dto)
    {
        var expected = File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, sample));
        var actual = JsonSerializer.Serialize(dto, dto.GetType(), Web);
        using var a = JsonDocument.Parse(expected);
        using var b = JsonDocument.Parse(actual);
        Assert.True(JsonElement.DeepEquals(a.RootElement, b.RootElement), $"{sample}\nexpected: {a.RootElement}\nactual:   {b.RootElement}");
    }

    private static TaxiTripSnapshot FullTrip() => new(
        "Uttarakhand Hills Trip",
        new TaxiSnapshotPickup("Kathgodam", D(18)),
        new TaxiSnapshotDrop("Kathgodam", true, D(21)),
        [
            new TaxiSnapshotCity(210000101, "Nainital", 3, D(18), D(21)),
            new TaxiSnapshotCity(210000102, "Ranikhet", 1, D(21), D(22)),
        ],
        [
            new TaxiSnapshotDay(1, D(18), "Nainital", true, 25, 370, ["Naini Lake", "Naina Devi Temple", "Snow View Point", "Mall Road"]),
            new TaxiSnapshotDay(2, D(19), "Nainital", true, 30, 560, ["Bhimtal Lake", "Sattal", "Kainchi Dham", "Nainital Zoo"]),
            new TaxiSnapshotDay(3, D(20), "Nainital", false, 40, 300, ["Tiffin Top", "Hanuman Garhi", "Himalaya Darshan", "Raj Bhavan"]),
            new TaxiSnapshotDay(4, D(21), "Ranikhet", true, 18, 230, ["Chaubatia Gardens", "Jhula Devi Temple", "Ranikhet Golf Course"]),
        ]);

    private static TaxiTripSnapshot CitiesOnly(params string[] names)
        => new("Trip", null, null, names.Select((n, i) => new TaxiSnapshotCity(210000200 + i, n, 1, D(1), D(2))).ToList(), []);

    private static DateOnly D(int day) => new(2026, 12, day);

    private static TaxiRequest Request(Guid id, string pickup, TaxiTripSnapshot trip, bool p, bool s, bool d, int sightseeingDays,
        DateOnly from, DateOnly to, short seats, short travellers) => new()
    {
        Id = id, TravellerUserId = Traveller, PickupCityName = pickup, WantPickup = p, WantSightseeing = s, WantDrop = d,
        SightseeingDaysJson = JsonSerializer.Serialize(Enumerable.Range(1, sightseeingDays)), TripSnapshotJson = trip.ToJson(),
        DateFrom = from, DateTo = to, Seats = seats, Travellers = travellers, Status = RequestStates.Collecting,
    };

    private static TaxiRequest RequestA_(TaxiTripSnapshot? trip = null)
        => Request(RequestA, "Kathgodam", trip ?? FullTrip(), true, true, true, 3, D(18), D(21), 4, 2);

    private static TaxiRequestOffer Offer(Guid id, Guid requestId, string state, string? endReason = null, DateTime? offeredAt = null) => new()
    {
        Id = id, RequestId = requestId, Status = state, EndReason = endReason, OfferedAt = offeredAt ?? Now.AddMinutes(-2),
        ExpiresAt = new DateTime(2026, 12, 10, 9, 46, 7, DateTimeKind.Utc),
    };

    private static TaxiBooking Booking(Guid id, Guid offerId, string state, DateOnly from, DateOnly to, string traveller = "Aarav Sharma") => new()
    {
        Id = id, OfferId = offerId, Status = state, DateFrom = from, DateTo = to, TravellerUserId = Traveller, TravellerName = traveller,
        PickupCityName = "Kathgodam", BookedAt = Now,
    };

    private static DriverOfferSource Source(TaxiRequestOffer o, TaxiRequest r, TaxiBooking? b = null, TaxiDateRange? conflict = null)
        => new(o, r, b, "Aarav Sharma", "+919876543210", conflict);

    [Fact]
    public void List_dto_equals_the_contract_sample()
    {
        var ringing = Source(Offer(OfferA, RequestA, OfferStates.Ringing), RequestA_());

        var bookedOffer = Guid.Parse("a1b2c3d4-0009-4a00-8000-000000000009");
        var bookedRequest = Request(Guid.Parse("5c8e1a70-9b2d-4d3f-a6e1-7f0b3c9d1e22"), "Kathgodam", CitiesOnly("Nainital"),
            true, false, true, 0, D(26), D(28), 6, 5);
        var booked = Source(Offer(bookedOffer, bookedRequest.Id, OfferStates.Confirmed), bookedRequest,
            Booking(Guid.Parse("b00c1e55-0009-4b00-8000-000000000009"), bookedOffer, BookingStates.Booked, D(26), D(28)));

        var missedRequest = Request(Guid.Parse("6d9f2b81-0c3e-4e4a-b7f2-8a1c4d0e2f33"), "Kathgodam", CitiesOnly("Nainital"),
            true, true, false, 2, D(12), D(13), 4, 3);
        var missed = Source(Offer(Guid.Parse("a1b2c3d4-0010-4a00-8000-000000000010"), missedRequest.Id, OfferStates.Missed, OfferEndReasons.Expired), missedRequest);

        var notSelectedRequest = Request(Guid.Parse("7e0a3c92-1d4f-4f5b-c8a3-9b2d5e1f3a44"), "Haldwani", CitiesOnly("Nainital", "Almora"),
            true, true, true, 4, D(5), D(9), 4, 2);
        var notSelected = Source(Offer(Guid.Parse("a1b2c3d4-0011-4a00-8000-000000000011"), notSelectedRequest.Id, OfferStates.NotSelected,
            OfferEndReasons.Booked), notSelectedRequest);

        var dto = TaxiDriverViews.BuildList(Now, [booked, ringing], [missed, notSelected], null);

        AssertEqualsSample("driver_offer_list.json", dto);
    }

    [Fact]
    public void Detail_dto_equals_both_contract_samples()
    {
        var ringing = TaxiDriverViews.BuildOffer(Source(Offer(OfferA, RequestA, OfferStates.Ringing), RequestA_()), withTrip: true);
        AssertEqualsSample("driver_offer_detail_ringing.json", ringing);

        var selectedOffer = Offer(OfferA, RequestA, OfferStates.Selected);
        selectedOffer.ConfirmBy = new DateTime(2026, 12, 10, 10, 1, 0, DateTimeKind.Utc);
        var selected = TaxiDriverViews.BuildOffer(Source(selectedOffer, RequestA_(), conflict: new TaxiDateRange(D(20), D(24))), withTrip: true);
        AssertEqualsSample("driver_offer_detail_selected.json", selected);
    }

    [Theory]
    [InlineData(OfferStates.Ringing, null)]
    [InlineData(OfferStates.Missed, null)]
    [InlineData(OfferStates.NotAgreed, null)]
    [InlineData(OfferStates.NotSelected, null)]
    [InlineData(OfferStates.Closed, null)]
    [InlineData(OfferStates.Withdrawn, BookingStates.Cancelled)]
    [InlineData(OfferStates.Confirmed, BookingStates.Completed)]
    [InlineData(OfferStates.Confirmed, BookingStates.Cancelled)]
    public void Traveller_is_null_before_accept_and_after_not_agreed(string offerState, string? bookingState)
    {
        var booking = bookingState is null ? null : Booking(Guid.NewGuid(), OfferA, bookingState, D(18), D(21));
        var dto = TaxiDriverViews.BuildOffer(Source(Offer(OfferA, RequestA, offerState), RequestA_(), booking), withTrip: true);
        Assert.Null(dto.Traveller);
        Assert.NotNull(dto.Trip);
    }

    [Theory]
    [InlineData(OfferStates.Accepted, null)]
    [InlineData(OfferStates.Selected, null)]
    [InlineData(OfferStates.Confirmed, BookingStates.Booked)]
    [InlineData(OfferStates.Confirmed, BookingStates.InProgress)]
    public void Traveller_is_shown_after_accept_while_the_booking_is_live(string offerState, string? bookingState)
    {
        var booking = bookingState is null ? null : Booking(Guid.NewGuid(), OfferA, bookingState, D(18), D(21));
        var dto = TaxiDriverViews.BuildOffer(Source(Offer(OfferA, RequestA, offerState), RequestA_(), booking), withTrip: false);
        Assert.Equal("Aarav Sharma", dto.Traveller!.Name);
        Assert.Equal("+919876543210", dto.Traveller.Phone);
        Assert.Null(dto.Trip);
    }

    [Fact]
    public void Dates_conflict_is_only_set_on_a_selected_offer()
    {
        var clash = new TaxiDateRange(D(20), D(24));
        Assert.Null(TaxiDriverViews.BuildOffer(Source(Offer(OfferA, RequestA, OfferStates.Accepted), RequestA_(), conflict: clash), false).DatesConflict);
        Assert.NotNull(TaxiDriverViews.BuildOffer(Source(Offer(OfferA, RequestA, OfferStates.Selected), RequestA_(), conflict: clash), false).DatesConflict);
    }

    [Fact]
    public void Declined_offers_are_not_listed()
    {
        var declined = Source(Offer(Guid.NewGuid(), RequestA, OfferStates.Declined), RequestA_());
        var ringing = Source(Offer(OfferA, RequestA, OfferStates.Ringing), RequestA_());

        var dto = TaxiDriverViews.BuildList(Now, [declined, ringing], [declined], null);

        Assert.Equal([OfferA], dto.Items.Select(i => i.OfferId).ToArray());
        Assert.Equal(1, dto.Counts.New);
        Assert.Null(OfferBuckets.For(OfferStates.Declined, false));
        Assert.DoesNotContain(OfferStates.Declined, TaxiDriverViews.EarlierSql(false));
    }

    [Fact]
    public void Earlier_page_is_keyset_on_offered_at_then_id()
    {
        var sql = TaxiDriverViews.EarlierSql(true);
        Assert.Contains("(o.\"OfferedAt\", o.\"Id\") < (@at, @id)", sql);
        Assert.Contains("ORDER BY o.\"OfferedAt\" DESC, o.\"Id\" DESC", sql);
        Assert.DoesNotContain("@at", TaxiDriverViews.EarlierSql(false));

        var cursor = new DriverOfferCursor(new DateTime(2026, 12, 10, 9, 41, 8, 123, DateTimeKind.Utc).AddTicks(4560), Guid.NewGuid());
        Assert.True(DriverOfferCursor.TryParse(cursor.Encode(), out var back));
        Assert.Equal(cursor, back);
        Assert.False(DriverOfferCursor.TryParse("not-a-cursor", out _));
        Assert.False(DriverOfferCursor.TryParse("", out _));
    }

    [Fact]
    public void Bookings_and_rating_dtos_equal_the_samples()
    {
        var other = Guid.NewGuid();
        var active = Booking(Guid.Parse("b00c1e55-0001-4b00-8000-000000000001"), OfferA, BookingStates.Booked, D(18), D(21));
        active.RequestId = RequestA;
        var done = Booking(Guid.Parse("b00c1e55-0007-4b00-8000-000000000007"), Guid.Parse("a1b2c3d4-0007-4a00-8000-000000000007"),
            BookingStates.Completed, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 6), "Neha Verma");
        done.RequestId = Guid.Parse("8f1b4da3-2e50-4a6c-d9b4-0c3e6f2a4b55");
        done.TravellerUserId = other;
        done.BookedAt = Now.AddDays(-50);
        var phones = new Dictionary<Guid, string?> { [Traveller] = "+919876543210", [other] = "+919811111111" };

        var bookings = TaxiDriverViews.BuildBookings(D(1), D(31), [done, active], phones);
        AssertEqualsSample("driver_bookings.json", bookings);

        TaxiRating R(int y, int m, int d, short stars) => new() { CreatedAt = new DateTime(y, m, d, 6, 0, 0, DateTimeKind.Utc), Stars = stars };
        var rating = TaxiDriverViews.BuildRating(346, 72, 86, [R(2026, 10, 12, 4), R(2026, 11, 2, 5), R(2026, 9, 30, 5), R(2026, 10, 24, 5)]);
        AssertEqualsSample("driver_rating.json", rating);

        Assert.Null(TaxiDriverViews.BuildRating(0, 0, 3, []).Average);
    }

    [Fact]
    public void In_progress_booking_can_end_and_only_active_ones_are_occupied()
    {
        var inProgress = Booking(Guid.NewGuid(), OfferA, BookingStates.InProgress, D(18), D(21));
        var cancelled = Booking(Guid.NewGuid(), OfferA, BookingStates.Cancelled, D(3), D(4));
        var phones = new Dictionary<Guid, string?> { [Traveller] = "+919876543210" };
        var dto = TaxiDriverViews.BuildBookings(D(1), D(31), [cancelled, inProgress], phones);
        Assert.Equal(inProgress.Id, dto.Items[0].BookingId);
        Assert.True(dto.Items[0].CanEnd);
        Assert.True(dto.Items[0].CanWithdraw);
        Assert.False(dto.Items[1].CanWithdraw);
        Assert.Null(dto.Items[1].TravellerPhone);
        Assert.Equal(inProgress.Id, Assert.Single(dto.Occupied).BookingId);
    }

    internal static TaxiListing Listing(DateTime? ringReadyAt, bool notificationsOn, short missStreak) => new()
    {
        Id = Guid.NewGuid(), DrivingLicenceNumberEnc = "v1.x.y", DateOfBirth = new DateOnly(1990, 1, 1), Status = TaxiStatuses.Approved,
        IsAvailable = true, RingReadyAt = ringReadyAt, RingReadyBuild = 69, RingNotificationsOn = notificationsOn, RingMissStreak = missStreak,
    };

    [Fact]
    public void Readiness_dto_equals_the_sample()
        => AssertEqualsSample("driver_readiness.json", TaxiDriverViews.BuildReadiness(Listing(Now, true, 0), true, 0));

    [Fact]
    public void Device_state_dto_equals_the_sample()
    {
        var text = File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, "driver_device_state.json"));
        var report = JsonSerializer.Deserialize<DriverDeviceStateDto>(text, Web)!;
        Assert.True(new DriverDeviceStateValidator().Validate(report).IsValid);
        AssertEqualsSample("driver_device_state.json", report);
        Assert.False(DriverReadinessFlow.ResetsMissedRings(report));

        Assert.False(new DriverDeviceStateValidator().Validate(new DriverDeviceStateDto { AppBuild = 0, AndroidSdk = 34 }).IsValid);
        Assert.False(new DriverDeviceStateValidator().Validate(new DriverDeviceStateDto { AppBuild = 69, AndroidSdk = 34, Manufacturer = new string('x', 65) }).IsValid);
    }

    [Fact]
    public void Driver_is_ring_ready_until_the_phone_reports_it_off()
    {
        Assert.True(TaxiDriverViews.BuildReadiness(Listing(null, false, 0), true, 0).RingReady);
        Assert.False(TaxiDriverViews.BuildReadiness(Listing(Now, false, 0), true, 0).RingReady);
        Assert.False(TaxiDriverViews.BuildReadiness(Listing(Now, true, 0), false, 0).RingReady);
        Assert.False(TaxiDriverViews.BuildReadiness(null, true, 0).RingReady);
    }

    [Fact]
    public void Driver_at_the_limit_is_not_ring_ready()
    {
        var below = TaxiDriverViews.BuildReadiness(Listing(Now, true, TaxiDispatchRules.RingMissLimit - 1), true, 0);
        Assert.True(below.RingReady);
        Assert.False(below.MissedRings);

        var at = TaxiDriverViews.BuildReadiness(Listing(Now, true, TaxiDispatchRules.RingMissLimit), true, 0);
        Assert.False(at.RingReady);
        Assert.True(at.MissedRings);
        Assert.True(TaxiDriverViews.BuildReadiness(Listing(Now, true, TaxiDispatchRules.RingMissLimit + 2), true, 0).MissedRings);
    }

    [Fact]
    public void Bookings_window_must_be_ordered_and_short()
    {
        var v = new DriverBookingsWindowValidator();
        Assert.True(v.Validate(new DriverBookingsWindow(D(1), D(31))).IsValid);
        Assert.False(v.Validate(new DriverBookingsWindow(null, D(31))).IsValid);
        Assert.False(v.Validate(new DriverBookingsWindow(D(31), D(1))).IsValid);
        Assert.False(v.Validate(new DriverBookingsWindow(D(1), D(1).AddDays(DriverBookingsWindowValidator.MaxDays))).IsValid);
    }

    [Fact]
    public void Owner_check_allows_only_the_offer_driver()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        Assert.Null(TaxiDriverRequestHandlers.OwnerRefusal(me, me));
        Assert.Equal(TaxiRequestErrorTypes.NotOfferDriver, TaxiDriverRequestHandlers.OwnerRefusal(other, me));
        Assert.Equal(TaxiRequestErrorTypes.OfferNotFound, TaxiDriverRequestHandlers.OwnerRefusal(null, me));
        Assert.Equal(403, TaxiDriverRequestHandlers.StatusOf(TaxiRequestErrorTypes.NotOfferDriver));
        Assert.Equal(404, TaxiDriverRequestHandlers.StatusOf(TaxiRequestErrorTypes.OfferNotFound));
    }
}

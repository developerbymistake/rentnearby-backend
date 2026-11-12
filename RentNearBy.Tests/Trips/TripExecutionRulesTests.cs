namespace RentNearBy.Tests.Trips;

public class TripExecutionRulesTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);

    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    private static TripOfferLine Line(int units, int price, int guests = 2) => new() { RoomName = "Deluxe", Units = units, Guests = guests, PricePerNight = price };

    [Fact]
    public void Constants_match_the_contract()
    {
        Assert.Equal(3, TripExecutionRules.MaxShortlistPerStep);
        Assert.Equal(3, TripExecutionRules.MaxRecipientsPerEnquiry);
        Assert.Equal(3, JourneyRules.MaxAskPerLeg);
        Assert.Equal(3, JourneyRules.MaxAskTaxis);
        Assert.Equal(3, TripExecutionRules.MaxOpenPerNight);
        Assert.Equal(3, TripExecutionRules.MaxOffersPerEnquiry);
        Assert.Equal(72, TripExecutionRules.EnquiryHours);
        Assert.Equal(TripExecutionRules.EnquiryHours, TripExecutionRules.ShortlistHours);
        Assert.Equal(12, TripExecutionRules.ReplyBeforeHours);
        Assert.Equal(2, TripExecutionRules.FloorHours);
        Assert.Equal(24, TripExecutionRules.OfferHours);
        Assert.Equal(2, TripExecutionRules.OfferUrgentHours);
        Assert.Equal(1, TripExecutionRules.OfferFloorHours);
        Assert.Equal(72, TripExecutionRules.UrgentHours);
        Assert.Equal(80, TripExecutionRules.MaxTextLen);
        Assert.Equal(8, TripExecutionRules.MaxSpotsInText);
        Assert.Equal(9_900_000, TripExecutionRules.MaxAmount);
        Assert.Equal(StayLimits.MaxPrice, TripExecutionRules.MaxAmount);
        Assert.Equal(20, TripExecutionRules.EnquiriesPerHour);
        Assert.Equal(60, TripExecutionRules.EnquiriesPerDay);
        Assert.Equal("X-Trip-Asks-Left", TripExecutionRules.AsksLeftHeader);
    }

    [Fact]
    public void ComputeTotal_saturates_instead_of_overflowing()
    {
        var lines = Enumerable.Range(0, 5).Select(_ => Line(10, 9_900_000)).ToList();
        var total = TripExecutionRules.ComputeTotal(lines, 14);
        Assert.Equal(int.MaxValue, total);
        Assert.True(total > TripExecutionRules.MaxAmount);
    }

    [Fact]
    public void IstMidnightUtc_is_the_previous_day_1830_utc()
    {
        Assert.Equal(Utc(2026, 10, 31, 18, 30), TripExecutionRules.IstMidnightUtc(Start));
    }

    [Fact]
    public void TodayIst_rolls_over_at_1830_utc()
    {
        Assert.Equal(new DateOnly(2026, 10, 31), TripExecutionRules.TodayIst(Utc(2026, 10, 31, 18, 29)));
        Assert.Equal(new DateOnly(2026, 11, 1), TripExecutionRules.TodayIst(Utc(2026, 10, 31, 18, 30)));
    }

    [Fact]
    public void NightDate_is_start_plus_day_minus_one()
    {
        Assert.Equal(Start, TripExecutionRules.NightDate(Start, 1));
        Assert.Equal(new DateOnly(2026, 11, 3), TripExecutionRules.NightDate(Start, 3));
    }

    [Fact]
    public void IsUrgent_boundary_is_72_hours_before_start_midnight_ist()
    {
        Assert.False(TripExecutionRules.IsUrgent(Start, Utc(2026, 10, 28, 18, 30)));
        Assert.True(TripExecutionRules.IsUrgent(Start, Utc(2026, 10, 28, 18, 31)));
        Assert.False(TripExecutionRules.IsUrgent(Start, Utc(2026, 10, 1)));
        Assert.True(TripExecutionRules.IsUrgent(Start, Utc(2026, 10, 31, 12)));
    }

    [Fact]
    public void EnquiryExpiry_far_before_trip_is_72_hours()
    {
        var (expiresAt, veryShort) = TripExecutionRules.EnquiryExpiry(Start, Start, Utc(2026, 10, 20, 6, 30));
        Assert.Equal(Utc(2026, 10, 23, 6, 30), expiresAt);
        Assert.False(veryShort);
    }

    [Fact]
    public void EnquiryExpiry_near_trip_is_12_hours_before_start()
    {
        var (expiresAt, veryShort) = TripExecutionRules.EnquiryExpiry(Start, Start, Utc(2026, 10, 31));
        Assert.Equal(Utc(2026, 10, 31, 6, 30), expiresAt);
        Assert.False(veryShort);
    }

    [Fact]
    public void EnquiryExpiry_floor_is_two_hours_and_flags_very_short_notice()
    {
        var (expiresAt, veryShort) = TripExecutionRules.EnquiryExpiry(Start, Start, Utc(2026, 10, 31, 12));
        Assert.Equal(Utc(2026, 10, 31, 14), expiresAt);
        Assert.True(veryShort);
    }

    [Fact]
    public void EnquiryExpiry_on_first_ist_day_uses_check_in_as_reference()
    {
        var (expiresAt, veryShort) = TripExecutionRules.EnquiryExpiry(Start, Start, Utc(2026, 10, 31, 19));
        Assert.Equal(Utc(2026, 10, 31, 21), expiresAt);
        Assert.True(veryShort);
    }

    [Fact]
    public void EnquiryExpiry_during_trip_uses_the_check_in_night()
    {
        var (expiresAt, veryShort) = TripExecutionRules.EnquiryExpiry(Start, new DateOnly(2026, 11, 3), Utc(2026, 11, 2));
        Assert.Equal(Utc(2026, 11, 2, 6, 30), expiresAt);
        Assert.False(veryShort);
    }

    [Fact]
    public void OfferValidUntil_normal_is_24_hours()
    {
        Assert.Equal(Utc(2026, 10, 21, 6, 30), TripExecutionRules.OfferValidUntil(Start, Start, Utc(2026, 10, 20, 6, 30)));
    }

    [Fact]
    public void OfferValidUntil_urgent_is_2_hours()
    {
        Assert.Equal(Utc(2026, 10, 30, 8), TripExecutionRules.OfferValidUntil(Start, Start, Utc(2026, 10, 30, 6)));
    }

    [Fact]
    public void OfferValidUntil_is_capped_by_check_in_midnight()
    {
        Assert.Equal(Utc(2026, 10, 31, 18, 30), TripExecutionRules.OfferValidUntil(Start, Start, Utc(2026, 10, 31, 17)));
    }

    [Fact]
    public void OfferValidUntil_floor_is_one_hour()
    {
        Assert.Equal(Utc(2026, 10, 31, 19), TripExecutionRules.OfferValidUntil(Start, Start, Utc(2026, 10, 31, 18)));
    }

    [Theory]
    [InlineData(1, 3, 2, 4, true)]
    [InlineData(1, 2, 2, 3, false)]
    [InlineData(1, 5, 2, 3, true)]
    [InlineData(3, 4, 1, 2, false)]
    [InlineData(1, 3, 1, 3, true)]
    public void NightsOverlap_is_half_open(int aIn, int aOut, int bIn, int bOut, bool expected)
    {
        static DateOnly D(int n) => new(2026, 11, n);
        Assert.Equal(expected, TripExecutionRules.NightsOverlap(D(aIn), D(aOut), D(bIn), D(bOut)));
    }

    [Fact]
    public void ComputeTotal_is_nights_times_sum_of_units_and_price()
    {
        var lines = new List<TripOfferLine> { Line(1, 1900), Line(2, 1000) };
        Assert.Equal(7800, TripExecutionRules.ComputeTotal(lines, 2));
    }

    [Fact]
    public void ComputeTotal_single_line_single_night()
    {
        Assert.Equal(1900, TripExecutionRules.ComputeTotal(new List<TripOfferLine> { Line(1, 1900) }, 1));
    }

    [Theory]
    [InlineData(TripEnquiryStatuses.Asked, null, TripDisplayStatuses.Asked)]
    [InlineData(TripEnquiryStatuses.Countered, null, TripDisplayStatuses.Asked)]
    [InlineData(TripEnquiryStatuses.Offered, null, TripDisplayStatuses.Offered)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, null, TripDisplayStatuses.ConfirmedByBoth)]
    [InlineData(TripEnquiryStatuses.NotAvailable, null, TripDisplayStatuses.NotAvailable)]
    [InlineData(TripEnquiryStatuses.Expired, null, TripDisplayStatuses.Expired)]
    [InlineData(TripEnquiryStatuses.Declined, null, TripDisplayStatuses.Declined)]
    [InlineData(TripEnquiryStatuses.Failed, TripFailReasons.DriverDenied, TripDisplayStatuses.NotMe)]
    [InlineData(TripEnquiryStatuses.CancelledByTraveller, null, TripDisplayStatuses.Cancelled)]
    [InlineData(TripEnquiryStatuses.CancelledByOwner, null, TripDisplayStatuses.Cancelled)]
    [InlineData(TripEnquiryStatuses.Closed, null, TripDisplayStatuses.Closed)]
    [InlineData(TripEnquiryStatuses.Closed, TripCloseReasons.Withdrawn, TripDisplayStatuses.Closed)]
    [InlineData(TripEnquiryStatuses.Closed, TripCloseReasons.BookedElsewhere, TripDisplayStatuses.Closed)]
    public void DisplayStatus_matrix(string status, string? failReason, string expected)
    {
        Assert.Equal(expected, TripExecutionRules.DisplayStatus(status, failReason));
    }

    [Fact]
    public void Display_status_wire_values_are_the_app_values()
    {
        Assert.Equal("asked", TripDisplayStatuses.Asked);
        Assert.Equal("offered", TripDisplayStatuses.Offered);
        Assert.Equal("confirmedByBoth", TripDisplayStatuses.ConfirmedByBoth);
        Assert.Equal("notAvailable", TripDisplayStatuses.NotAvailable);
        Assert.Equal("expired", TripDisplayStatuses.Expired);
        Assert.Equal("declined", TripDisplayStatuses.Declined);
        Assert.Equal("cancelled", TripDisplayStatuses.Cancelled);
        Assert.Equal("notMe", TripDisplayStatuses.NotMe);
        Assert.Equal("closed", TripDisplayStatuses.Closed);
    }

    [Fact]
    public void Status_sets_are_consistent()
    {
        foreach (var s in TripEnquiryStatuses.All)
            Assert.Equal(!TripEnquiryStatuses.IsOpen(s), TripEnquiryStatuses.IsTerminal(s));
        foreach (var s in TripEnquiryStatuses.Unconfirmed.Concat(TripEnquiryStatuses.Confirmed))
            Assert.True(TripEnquiryStatuses.IsOpen(s));
        Assert.Empty(TripEnquiryStatuses.Unconfirmed.Intersect(TripEnquiryStatuses.Confirmed));
        Assert.False(TripEnquiryStatuses.IsOpen(TripEnquiryStatuses.Expired));
        Assert.False(TripEnquiryStatuses.IsOpen(TripEnquiryStatuses.Closed));
        Assert.Equal(TripEnquiryStatuses.All.Length, TripEnquiryStatuses.All.Distinct().Count());
    }

    [Fact]
    public void OpenSqlList_lists_exactly_the_open_statuses()
    {
        foreach (var s in TripEnquiryStatuses.All)
            Assert.Equal(TripEnquiryStatuses.IsOpen(s), TripEnquiryStatuses.OpenSqlList.Contains($"'{s}'"));
    }

    [Fact]
    public void Completed_is_not_part_of_the_savable_trip_statuses()
    {
        Assert.Equal("Completed", TripStatuses.Completed);
        Assert.DoesNotContain(TripStatuses.Completed, TripStatuses.Savable);
    }
}

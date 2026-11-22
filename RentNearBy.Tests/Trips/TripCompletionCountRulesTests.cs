using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.Trips;

// FB-W1 / FB-W2: the pure counting rules (D-03..D-06).
public class TripCompletionCountRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 22);

    [Fact]
    public void Trip_without_end_date_never_counts()
        => Assert.False(TripCompletionCountRules.Counts(null, Today, true, false));

    [Fact]
    public void Trip_ending_today_does_not_count()
        => Assert.False(TripCompletionCountRules.Counts(Today, Today, true, false));

    [Fact]
    public void Trip_ended_yesterday_with_confirmed_booking_counts()
        => Assert.True(TripCompletionCountRules.Counts(Today.AddDays(-1), Today, true, false));

    [Fact]
    public void Trip_without_in_app_confirmed_booking_does_not_count()
        => Assert.False(TripCompletionCountRules.Counts(Today.AddDays(-1), Today, false, false));

    [Fact]
    public void Trip_already_counted_never_counts_again()
        => Assert.False(TripCompletionCountRules.Counts(Today.AddDays(-5), Today, true, true));

    [Theory]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, true)]
    [InlineData(TripEnquiryStatuses.BookedByTraveller, false)]
    [InlineData(TripEnquiryStatuses.Offered, false)]
    [InlineData(TripEnquiryStatuses.FareSent, false)]
    [InlineData(TripEnquiryStatuses.CancelledByOwner, false)]
    [InlineData(TripEnquiryStatuses.Closed, false)]
    public void Only_confirmed_by_both_enquiry_is_in_app_confirmed(string status, bool expected)
        => Assert.Equal(expected, TripCompletionCountRules.IsInAppConfirmedEnquiry(status));

    [Theory]
    [InlineData(BookingStates.Booked, true)]
    [InlineData(BookingStates.InProgress, true)]
    [InlineData(BookingStates.Completed, true)]
    [InlineData(BookingStates.Cancelled, false)]
    public void Taxi_booking_confirmed_by_the_driver_is_in_app_confirmed(string status, bool expected)
        => Assert.Equal(expected, TripCompletionCountRules.IsInAppConfirmedTaxiBooking(status));

    [Fact]
    public void Host_counts_for_a_confirmed_enquiry_after_check_out_of_a_completed_trip()
        => Assert.True(TripCompletionCountRules.HostCounts(TripEnquiryStatuses.ConfirmedByBoth, Today.AddDays(-1), Today, true, false));

    [Fact]
    public void Host_does_not_count_before_check_out()
    {
        Assert.False(TripCompletionCountRules.HostCounts(TripEnquiryStatuses.ConfirmedByBoth, Today, Today, true, false));
        Assert.False(TripCompletionCountRules.HostCounts(TripEnquiryStatuses.ConfirmedByBoth, null, Today, true, false));
    }

    [Theory]
    [InlineData(TripEnquiryStatuses.Offered)]
    [InlineData(TripEnquiryStatuses.CancelledByTraveller)]
    [InlineData(TripEnquiryStatuses.Closed)]
    [InlineData(TripEnquiryStatuses.BookedByTraveller)]
    public void Host_never_counts_an_unconfirmed_enquiry(string status)
        => Assert.False(TripCompletionCountRules.HostCounts(status, Today.AddDays(-3), Today, true, false));

    [Fact]
    public void Host_does_not_count_while_the_trip_is_open_or_already_counted()
    {
        Assert.False(TripCompletionCountRules.HostCounts(TripEnquiryStatuses.ConfirmedByBoth, Today.AddDays(-3), Today, false, false));
        Assert.False(TripCompletionCountRules.HostCounts(TripEnquiryStatuses.ConfirmedByBoth, Today.AddDays(-3), Today, true, true));
    }

    [Fact]
    public void Taxi_booking_counts_only_when_completed_after_a_started_ride()
    {
        Assert.True(TripCompletionCountRules.TaxiBookingCounts(BookingStates.Completed, DateTime.UtcNow, false));
        Assert.False(TripCompletionCountRules.TaxiBookingCounts(BookingStates.Completed, null, false));
        Assert.False(TripCompletionCountRules.TaxiBookingCounts(BookingStates.Cancelled, DateTime.UtcNow, false));
        Assert.False(TripCompletionCountRules.TaxiBookingCounts(BookingStates.InProgress, DateTime.UtcNow, false));
        Assert.False(TripCompletionCountRules.TaxiBookingCounts(BookingStates.Completed, DateTime.UtcNow, true));
    }
}

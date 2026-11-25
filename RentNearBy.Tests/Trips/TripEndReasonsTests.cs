namespace RentNearBy.Tests.Trips;

public class TripEndReasonsTests
{
    [Theory]
    [InlineData(TripEnquiryStatuses.Closed, TripCloseReasons.Withdrawn, false, "withdrawn")]
    [InlineData(TripEnquiryStatuses.Closed, TripCloseReasons.BookedElsewhere, false, "bookedElsewhere")]
    [InlineData(TripEnquiryStatuses.Closed, TripCloseReasons.TripDatesChanged, false, "datesChanged")]
    [InlineData(TripEnquiryStatuses.Closed, TripCloseReasons.TripDeleted, false, "tripDeleted")]
    [InlineData(TripEnquiryStatuses.Closed, TripCloseReasons.AccountDeleted, false, "accountDeleted")]
    [InlineData(TripEnquiryStatuses.Closed, TripCloseReasons.AdminClosed, false, "adminClosed")]
    [InlineData(TripEnquiryStatuses.Closed, null, false, "closed")]
    [InlineData(TripEnquiryStatuses.NotAvailable, TripFailReasons.ListingRemoved, false, "stayRemoved")]
    [InlineData(TripEnquiryStatuses.CancelledByOwner, TripFailReasons.ListingRemoved, false, "stayRemoved")]
    [InlineData(TripEnquiryStatuses.NotAvailable, "StayPaused", false, "stayPaused")]
    [InlineData(TripEnquiryStatuses.NotAvailable, "HostBlocked", false, "hostBlocked")]
    [InlineData(TripEnquiryStatuses.NotAvailable, null, false, "hostNotAvailable")]
    [InlineData(TripEnquiryStatuses.Expired, null, true, "offerExpired")]
    [InlineData(TripEnquiryStatuses.Expired, null, false, "noReply")]
    [InlineData(TripEnquiryStatuses.Declined, null, false, "declinedByYou")]
    [InlineData(TripEnquiryStatuses.CancelledByTraveller, null, false, "cancelledByGuest")]
    [InlineData(TripEnquiryStatuses.CancelledByOwner, null, false, "cancelledByHost")]
    [InlineData(TripEnquiryStatuses.Failed, TripFailReasons.ProblemHostNoRoom, false, "problem")]
    public void Every_end_has_an_honest_reason(string status, string? failReason, bool expiredOffer, string expected)
        => Assert.Equal(expected, TripEndReasons.Of(status, failReason, expiredOffer));

    [Theory]
    [InlineData(TripEnquiryStatuses.Asked)]
    [InlineData(TripEnquiryStatuses.Offered)]
    [InlineData(TripEnquiryStatuses.Countered)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth)]
    public void Open_rows_have_no_end_reason(string status) => Assert.Null(TripEndReasons.Of(status, null, true));

    [Fact]
    public void New_fail_reasons_fit_the_column()
    {
        Assert.True(TripEndReasons.StayPaused.Length <= 24);
        Assert.True(TripEndReasons.HostBlocked.Length <= 24);
    }
}

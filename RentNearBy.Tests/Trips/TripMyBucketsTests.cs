namespace RentNearBy.Tests.Trips;

public class TripMyBucketsTests
{
    private static readonly DateOnly Today = new(2026, 11, 25);

    [Theory]
    [InlineData(TripEnquiryStatuses.Asked, "waiting")]
    [InlineData(TripEnquiryStatuses.Countered, "waiting")]
    [InlineData(TripEnquiryStatuses.Offered, "offers")]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, "booked")]
    [InlineData(TripEnquiryStatuses.Expired, "past")]
    [InlineData(TripEnquiryStatuses.Closed, "past")]
    [InlineData(TripEnquiryStatuses.NotAvailable, "past")]
    [InlineData(TripEnquiryStatuses.Declined, "past")]
    [InlineData(TripEnquiryStatuses.CancelledByOwner, "past")]
    public void Status_maps_to_one_box(string status, string bucket)
        => Assert.Equal(bucket, TripMyBuckets.Of(status, new DateOnly(2026, 12, 2), Today));

    [Fact]
    public void A_booking_whose_check_out_passed_is_past()
    {
        Assert.Equal(TripMyBuckets.Past, TripMyBuckets.Of(TripEnquiryStatuses.ConfirmedByBoth, new DateOnly(2026, 11, 24), Today));
        Assert.Equal(TripMyBuckets.Booked, TripMyBuckets.Of(TripEnquiryStatuses.ConfirmedByBoth, Today, Today));
        Assert.Equal(TripMyBuckets.Booked, TripMyBuckets.Of(TripEnquiryStatuses.ConfirmedByBoth, null, Today));
    }

    [Theory]
    [InlineData("waiting", true)]
    [InlineData("offers", true)]
    [InlineData("booked", true)]
    [InlineData("past", true)]
    [InlineData("closed", false)]
    [InlineData(null, false)]
    public void Only_the_four_boxes_are_valid(string? bucket, bool valid) => Assert.Equal(valid, TripMyBuckets.IsValid(bucket));
}

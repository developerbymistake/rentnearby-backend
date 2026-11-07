namespace RentNearBy.Tests.Trips;

public class TripFinalFixBaTests
{
    [Theory]
    [InlineData("Planned", "Draft", 3, "Planned")]
    [InlineData("Planned", "Draft", 0, "Draft")]
    [InlineData("Draft", "Draft", 3, "Draft")]
    [InlineData("Draft", "Planned", 3, "Planned")]
    [InlineData("Planned", "Planned", 3, "Planned")]
    public void Save_never_puts_draft_over_a_planned_trip_with_spots(string stored, string requested, int spots, string expected)
        => Assert.Equal(expected, TripStatusRules.OnSave(stored, requested, spots));

    [Fact]
    public void Save_without_status_keeps_the_stored_one()
        => Assert.Null(TripStatusRules.OnSave(TripStatuses.Planned, null, 3));

    [Theory]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, TripOwnerBuckets.Confirmed)]
    [InlineData(TripEnquiryStatuses.Countered, TripOwnerBuckets.New)]
    [InlineData(TripEnquiryStatuses.Offered, TripOwnerBuckets.Offered)]
    [InlineData(TripEnquiryStatuses.Failed, TripOwnerBuckets.Closed)]
    public void Owner_bucket_maps_status(string status, string bucket)
        => Assert.Equal(bucket, TripOwnerBuckets.Of(status));
}

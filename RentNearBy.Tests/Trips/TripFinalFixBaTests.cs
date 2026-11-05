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
    [InlineData(TripEnquiryStatuses.ConfirmedByTraveller, TripOwnerBuckets.New)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, TripOwnerBuckets.Confirmed)]
    [InlineData(TripEnquiryStatuses.Countered, TripOwnerBuckets.New)]
    [InlineData(TripEnquiryStatuses.Offered, TripOwnerBuckets.Offered)]
    [InlineData(TripEnquiryStatuses.Failed, TripOwnerBuckets.Closed)]
    public void Owner_bucket_puts_a_phone_booking_waiting_for_the_host_in_new(string status, string bucket)
        => Assert.Equal(bucket, TripOwnerBuckets.Of(status));

    [Fact]
    public void Needs_text_asks_for_a_ground_floor_room()
        => Assert.Equal("a ground-floor room and vegetarian food",
            TripEnquiryText.NeedsText([TripNeeds.NoStairs, TripNeeds.VegFood]));
}

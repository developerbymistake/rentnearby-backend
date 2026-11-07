namespace RentNearBy.Tests.Trips;

public class JourneySaveRulesTests
{
    [Fact]
    public void Label_is_cut_to_the_column_width()
        => Assert.True(TaxiWaysLabels.Of(new string('a', 200), "v:1:2", true, false).Length <= JourneyRules.NameMax);

    [Fact]
    public void Label_of_a_url_only_name_falls_back()
        => Assert.Equal(TaxiWaysLabels.Pickup, TaxiWaysLabels.Of("https://example.com", "v:1:2", true, false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_label_never_returns_empty(string? typed)
    {
        Assert.NotEmpty(TaxiWaysLabels.Of(typed, null, false, false));
        Assert.NotEmpty(TaxiWaysLabels.Of(typed, "m:1:2", true, false));
    }

    [Fact]
    public void Group_cap_matches_the_schema_check() => Assert.Equal(12, JourneyRules.MaxPeople);

    [Fact]
    public void Seat_cover_with_unknown_driver_seats_keeps_the_driver_alone()
    {
        var r = TaxiSeatCover.SolveWith(new SeatOption(0, null, 3000), [new SeatOption(1, 4, 2000)], 5);
        Assert.Equal(new[] { 0 }, r.Chosen.ToArray());
        Assert.Equal(3000, r.Fare);
    }

    [Fact]
    public void Journey_without_asks_is_not_asked()
    {
        var legs = new[] { new SavedJourneyLeg { LegIndex = 1, DriverAvailable = true } };
        Assert.Equal(JourneyStatuses.NotAsked, JourneyRules.JourneyStatusOf(legs));
    }

    [Fact]
    public void Paused_driver_of_a_booked_car_does_not_make_the_journey_unavailable()
    {
        var legs = new[] { new SavedJourneyLeg { LegIndex = 1, DriverAvailable = false, LastEnquiryId = Guid.NewGuid() } };
        Assert.Equal(JourneyStatuses.DriverUnavailable, JourneyRules.JourneyStatusOf(legs));
        Assert.Equal(JourneyStatuses.Ready, JourneyRules.JourneyStatusOf(legs, new HashSet<int> { 1 }));
    }
}

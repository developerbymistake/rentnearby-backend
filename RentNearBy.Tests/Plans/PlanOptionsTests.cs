namespace RentNearBy.Tests.Plans;

public class PlanOptionsTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(3, 90)]
    [InlineData(12, 365)]
    [InlineData(6, 180)]
    [InlineData(0, 0)]
    public void PlanCyclesDaysOf_Months_ReturnsDays(int months, int expected)
        => Assert.Equal(expected, PlanCycles.DaysOf(months));

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(12, true)]
    [InlineData(2, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(24, false)]
    public void PlanCyclesIsValid_Months_MatchesSupportedCycles(int months, bool expected)
        => Assert.Equal(expected, PlanCycles.IsValid(months));

    [Fact]
    public void PlanCyclesAll_EveryEntry_IsValid()
        => Assert.All(PlanCycles.All, m => Assert.True(PlanCycles.IsValid(m)));

    [Theory]
    [InlineData("Room", true)]
    [InlineData("Plot", true)]
    [InlineData("Stay", true)]
    [InlineData("Taxi", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("room", false)]
    public void PlanFeaturesIsValid_Key_IsCaseSensitive(string? featureKey, bool expected)
        => Assert.Equal(expected, PlanFeatures.IsValid(featureKey));

    [Theory]
    [InlineData("Room", true)]
    [InlineData("Plot", true)]
    [InlineData("Stay", false)]
    [InlineData("Taxi", false)]
    [InlineData(null, false)]
    public void PlanFeaturesHasListingSlots_Key_OnlyRoomsAndPlots(string? featureKey, bool expected)
        => Assert.Equal(expected, PlanFeatures.HasListingSlots(featureKey));
}

namespace RentNearBy.Tests.Plans;

public class PlanFeatureValuesTests
{
    [Fact]
    public void From_AllKeys_MapsTypedValues()
    {
        var values = PlanFeatureValues.From(new Dictionary<string, int>
        {
            [PlanFeatureKeys.ListingLimit] = 3,
            [PlanFeatureKeys.Priority] = 2,
            [PlanFeatureKeys.Featured] = 1,
        });

        Assert.Equal(new PlanFeatureValues(3, 2, true, 0), values);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(-1, false)]
    public void From_FeaturedValue_IsTrueOnlyAboveZero(int featured, bool expected)
    {
        var values = PlanFeatureValues.From(new Dictionary<string, int> { [PlanFeatureKeys.Featured] = featured });

        Assert.Equal(expected, values.Featured);
    }

    [Fact]
    public void From_Empty_ReadsZeros()
        => Assert.Equal(new PlanFeatureValues(0, 0, false, 0), PlanFeatureValues.From(new Dictionary<string, int>()));

    [Fact]
    public void Fallback_IsOneListingAndOneJobPost()
        => Assert.Equal(new PlanFeatureValues(1, 0, false, 1), PlanFeatureValues.Fallback);

    [Fact]
    public void Max_TwoPlans_TakesElementWiseMaximum()
    {
        var a = new PlanFeatureValues(1, 0, false, 1);
        var b = new PlanFeatureValues(3, 2, true, 0);

        Assert.Equal(new PlanFeatureValues(3, 2, true, 1), PlanFeatureValues.Max(a, b));
    }

    [Fact]
    public void Max_SwappedArguments_SameResult()
    {
        var a = new PlanFeatureValues(1, 5, false, 4);
        var b = new PlanFeatureValues(3, 2, true, 0);

        Assert.Equal(PlanFeatureValues.Max(a, b), PlanFeatureValues.Max(b, a));
    }
}

namespace RentNearBy.Tests.Listings;

public class BighaAreaTests
{
    // Finding 32: Nainital, 1 bigha (~6,804 sqft) must sort below a 10,000 sqft plot.
    [Fact]
    public void Uttarakhand_OneBigha_IsSmallerThanTenThousandSqft()
    {
        var bigha = BighaArea.ToSqft(1m, "bigha", "Uttarakhand");
        Assert.Equal(6804m, bigha);
        Assert.True(bigha < BighaArea.ToSqft(10000m, "sqft", "Uttarakhand"));
    }

    [Theory]
    [InlineData("Uttarakhand", 6804)]
    [InlineData("Himachal Pradesh", 8712)]
    [InlineData("Rajasthan", 27225)]
    [InlineData("Uttar Pradesh", 27000)]
    [InlineData(" himachal pradesh ", 8712)]
    public void Bigha_PerState(string state, int sqft)
        => Assert.Equal((decimal)sqft, BighaArea.BighaSqft(state));

    [Theory]
    [InlineData("Kerala")]
    [InlineData("")]
    [InlineData(null)]
    public void Bigha_UnknownState_KeepsTheOldFactor(string? state)
        => Assert.Equal(27000m, BighaArea.BighaSqft(state));

    [Theory]
    [InlineData("sqft", 1200, 1200)]
    [InlineData("acre", 2, 87120)]
    [InlineData("nali", 1, 2152.78)]
    [InlineData("unknown", 7, 7)]
    public void OtherUnits_DoNotDependOnTheState(string unit, double value, double expected)
    {
        Assert.Equal((decimal)expected, BighaArea.ToSqft((decimal)value, unit, "Uttarakhand"));
        Assert.Equal((decimal)expected, BighaArea.ToSqft((decimal)value, unit, "Rajasthan"));
    }

    [Fact]
    public void HimachalBigha_IsNotRankedAsThreeTimesItsSize()
        => Assert.Equal(17424m, BighaArea.ToSqft(2m, "bigha", "Himachal Pradesh"));
}

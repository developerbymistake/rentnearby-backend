namespace RentNearBy.Tests.Trips;

public class CityKeysTests
{
    [Fact]
    public void FirstKey_MatchesAtlas()
        => Assert.Equal(110000001L, CityKeys.FromIndex(0));

    [Theory]
    [InlineData(1, 110000002L)]
    [InlineData(2, 110000003L)]
    [InlineData(183000, 110183001L)]
    public void Keys_StepByOne(int index, long expected)
        => Assert.Equal(expected, CityKeys.FromIndex(index));

    [Fact]
    public void Keys_AreConsecutive()
        => Assert.All(Enumerable.Range(0, 100), i => Assert.Equal(110000001L + i, CityKeys.FromIndex(i)));
}

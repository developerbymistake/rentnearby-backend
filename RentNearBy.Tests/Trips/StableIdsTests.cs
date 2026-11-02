namespace RentNearBy.Tests.Trips;

public class StableIdsTests
{
    [Fact]
    public void District_SamePosition_SameId()
        => Assert.Equal(StableIds.District(5), StableIds.District(5));

    [Fact]
    public void City_SameKey_SameId()
        => Assert.Equal(StableIds.City(110000001L), StableIds.City(110000001L));

    [Fact]
    public void Ids_AreFixedValues_NeverChangeBetweenReleases()
    {
        Assert.Equal(Guid.Parse("fdd36ed4-90b2-547c-a735-ba175ca0e511"), StableIds.District(0));
        Assert.Equal(Guid.Parse("d2e2b94d-f3a7-5350-acda-23414cbe0b03"), StableIds.City(110000001L));
    }

    [Fact]
    public void DistrictAndCity_DoNotShareIds()
        => Assert.NotEqual(StableIds.District(110000001), StableIds.City(110000001L));

    [Fact]
    public void Ids_UseVersion5AndRfcVariant()
    {
        var text = StableIds.District(3).ToString();
        Assert.Equal('5', text[14]);
        Assert.Contains(text[19], "89ab");
    }

    [Fact]
    public void NoCollisions_AcrossTheSeedRange()
    {
        var ids = new HashSet<Guid>();
        for (var i = 0; i < 1000; i++) Assert.True(ids.Add(StableIds.District(i)));
        for (var i = 0; i < 200_000; i++) Assert.True(ids.Add(StableIds.City(110000001L + i)));
    }

    [Fact]
    public void NeighbouringInputs_GiveDifferentIds()
    {
        Assert.NotEqual(StableIds.District(1), StableIds.District(2));
        Assert.NotEqual(StableIds.City(110000001L), StableIds.City(110000002L));
    }
}

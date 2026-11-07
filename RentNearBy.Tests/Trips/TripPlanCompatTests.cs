namespace RentNearBy.Tests.Trips;

public class TripPlanCompatTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    public void StoredVersionUpToCurrent_IsAccepted(int storedVersion)
        => Assert.False(TripPlanCompat.NeedsUpdate(storedVersion));

    [Fact]
    public void DocumentFromANewerServer_NeedsUpdate()
        => Assert.True(TripPlanCompat.NeedsUpdate(TripPlan.CurrentVersion + 1));

    [Fact]
    public void DefaultTitle_OneCity()
        => Assert.Equal("Haridwar trip", TripTitles.Default(["Haridwar"]));

    [Fact]
    public void DefaultTitle_TwoCities()
        => Assert.Equal("Haridwar, Badrinath trip", TripTitles.Default(["Haridwar", "Badrinath"]));

    [Fact]
    public void DefaultTitle_LongNames_ShowFirstPlusMore()
    {
        var names = new[] { new string('a', 30), new string('b', 30), new string('c', 30) };

        Assert.Equal($"{names[0]} + 2 more trip", TripTitles.Default(names));
    }

    [Fact]
    public void DefaultTitle_CappedAt80()
    {
        Assert.Equal(TripTitles.MaxLength, TripTitles.Default([new string('a', 100)]).Length);
        Assert.Equal(TripTitles.MaxLength, TripTitles.Default([new string('a', 100), "Badrinath"]).Length);
    }

    [Fact]
    public void DefaultTitle_ExactlyEightyKeepsAllCities()
    {
        var first = new string('a', 60);
        var second = new string('b', 80 - 60 - ", ".Length - " trip".Length);

        Assert.Equal($"{first}, {second} trip", TripTitles.Default([first, second]));
    }
}

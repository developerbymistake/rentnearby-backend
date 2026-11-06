namespace RentNearBy.Tests.Trips;

public class TripPlanCompatTests
{
    private static TripPlan Plan(params string[] cities)
    {
        var plan = new TripPlan
        {
            Legs = cities.Select((c, i) => new TripCityLeg { Index = i + 1, CityKey = 210000001 + i, CityName = c }).ToList(),
        };
        for (var i = 0; i < cities.Length; i++) plan.Days.Add(new TripDay { Day = i + 1, Leg = i + 1 });
        return plan;
    }

    private static TripPlan Legless(int version) => new() { Version = version, CityKey = 210000001, CityName = "Haridwar" };

    [Theory]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    [InlineData(3, 3)]
    public void LeglessOntoTwoLegs_NeedsUpdate(int requestVersion, int storedVersion)
        => Assert.True(TripPlanCompat.NeedsUpdate(Plan("Haridwar", "Badrinath"), Legless(requestVersion), storedVersion));

    [Theory]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    [InlineData(3, 3)]
    [InlineData(2, 4)]
    public void LeglessOntoOneLeg_IsAccepted(int requestVersion, int storedVersion)
        => Assert.False(TripPlanCompat.NeedsUpdate(Plan("Haridwar"), Legless(requestVersion), storedVersion));

    [Fact]
    public void LegsOntoTwoLegs_IsAccepted()
        => Assert.False(TripPlanCompat.NeedsUpdate(Plan("Haridwar", "Badrinath"), Plan("Haridwar", "Badrinath"), 4));

    [Fact]
    public void EmptyLegs_CountAsLegless()
    {
        var request = Legless(4);
        request.Legs = [];

        Assert.True(TripPlanCompat.NeedsUpdate(Plan("Haridwar", "Badrinath"), request, 4));
    }

    [Fact]
    public void OneLegWithArrival_IsMultiCityData()
    {
        var stored = Plan("Haridwar");
        stored.Legs![0].Arrival = new TripTravel { FromName = "Delhi" };

        Assert.True(TripPlanCompat.HasMultiCityData(stored));
        Assert.True(TripPlanCompat.NeedsUpdate(stored, Legless(3), 4));
    }

    [Fact]
    public void DocumentFromANewerServer_NeedsUpdate()
        => Assert.True(TripPlanCompat.NeedsUpdate(Plan("Haridwar"), Plan("Haridwar"), TripPlan.CurrentVersion + 1));

    [Fact]
    public void LeglessStoredPlan_HasNoMultiCityData()
        => Assert.False(TripPlanCompat.HasMultiCityData(Legless(3)));

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

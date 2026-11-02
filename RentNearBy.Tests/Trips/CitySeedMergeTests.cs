using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Tests.Trips;

public class CitySeedMergeTests
{
    private static DataSeeder.CitySeedRow Row(long key, string name, string district)
        => new(key, name, district, 29.0, 79.0, "Ok");

    [Fact]
    public void FileRows_KeyByIndex_CountedBeforeSkips()
    {
        var records = new List<DataSeeder.CitySeedRecord?>
        {
            new("Nainital", "Nainital", 29.39, 79.45),
            null,
            new("", "Nainital", 29.0, 79.0),
            new("Bhowali", "Nainital", 29.38, 79.51),
        };

        var rows = DataSeeder.RowsFromFile(records);

        Assert.Equal(new[] { CityKeys.FromIndex(0), CityKeys.FromIndex(2), CityKeys.FromIndex(3) }, rows.Select(r => r.CityKey));
        Assert.All(rows, r => Assert.Equal("Ok", r.SeedStatus));
        Assert.Equal("Bhowali", rows[2].Name);
    }

    [Fact]
    public void AtlasRows_KeepOkAndDuplicate_SkipRest()
    {
        var items = new[]
        {
            new AtlasExportCity { CityKey = 110000001, Name = "Nainital", DistrictName = "Nainital", SeedStatus = "Ok" },
            new AtlasExportCity { CityKey = 110000003, Name = "Bhowali", DistrictName = "Nainital", SeedStatus = "Duplicate" },
            new AtlasExportCity { CityKey = 110000005, Name = "Ramgarh", DistrictName = "Nowhere", SeedStatus = "NoDistrict" },
            new AtlasExportCity { CityKey = 110000007, Name = "Aurangabad", DistrictName = "Aurangabad", SeedStatus = "AmbiguousDistrict" },
        };

        var rows = DataSeeder.RowsFromAtlas(items).ToList();

        Assert.Equal(new long[] { 110000001, 110000003 }, rows.Select(r => r.CityKey).ToArray());
    }

    [Fact]
    public void AtlasRows_NullItems_Empty()
        => Assert.Empty(DataSeeder.RowsFromAtlas(null));

    [Fact]
    public void MatchKey_IgnoresCaseAndSpaces()
        => Assert.Equal(DataSeeder.MatchKey(" Nainital ", "BHOWALI"), DataSeeder.MatchKey("nainital", "bhowali"));

    [Fact]
    public void Match_ByDistrictAndName()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var cities = new List<(Guid, string, string)> { (a, "Nainital", "Nainital"), (b, "Dehradun", "Rishikesh") };

        var keys = DataSeeder.MatchCityKeys(cities, new[]
        {
            Row(110000001, "nainital", "NAINITAL"),
            Row(110000003, "Rishikesh", "Tehri Garhwal"),
        });

        Assert.Equal(110000001L, Assert.Single(keys, k => k.Key == a).Value);
        Assert.False(keys.ContainsKey(b));
    }

    [Fact]
    public void Match_FirstRowWins()
    {
        var a = Guid.NewGuid();
        var cities = new List<(Guid, string, string)> { (a, "Nainital", "Bhowali") };

        var keys = DataSeeder.MatchCityKeys(cities, new[]
        {
            Row(110000011, "Bhowali", "Nainital"),
            Row(110000013, "Bhowali", "Nainital"),
        });

        Assert.Equal(110000011L, keys[a]);
    }

    [Fact]
    public void Match_DuplicateLocalCities_LeftUnmatched()
    {
        var cities = new List<(Guid, string, string)>
        {
            (Guid.NewGuid(), "Nainital", "Bhowali"),
            (Guid.NewGuid(), "nainital", "bhowali"),
        };

        Assert.Empty(DataSeeder.MatchCityKeys(cities, new[] { Row(110000011, "Bhowali", "Nainital") }));
    }

    [Fact]
    public void Match_KeyUsedOnce()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var cities = new List<(Guid, string, string)> { (a, "Nainital", "Bhowali"), (b, "Almora", "Ranikhet") };

        var keys = DataSeeder.MatchCityKeys(cities, new[]
        {
            Row(110000011, "Bhowali", "Nainital"),
            Row(110000011, "Ranikhet", "Almora"),
        });

        Assert.Equal(110000011L, keys[a]);
        Assert.False(keys.ContainsKey(b));
    }
}

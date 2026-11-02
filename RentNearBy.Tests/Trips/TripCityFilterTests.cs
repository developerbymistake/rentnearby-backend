using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

public class TripCityFilterTests
{
    private static readonly List<TripCityInfo> Cities =
    [
        new() { CityKey = 1, Name = "Almora", DistrictName = "Almora" },
        new() { CityKey = 2, Name = "Kausani", DistrictName = "Bageshwar" },
        new() { CityKey = 3, Name = "Nainital", DistrictName = "Nainital" },
        new() { CityKey = 4, Name = "Ranikhet", DistrictName = "Almora" },
        new() { CityKey = 5, Name = "Rishikesh", DistrictName = "Dehradun" },
    ];

    private static List<string> Names(string? q) => TripCityCatalog.FilterCities(Cities, q).Select(c => c.Name).ToList();

    [Fact]
    public void PrefixBeforeContains()
        => Assert.Equal(new[] { "Ranikhet", "Almora", "Rishikesh" }, Names("ra"));

    [Fact]
    public void DistrictNameContains()
        => Assert.Equal(new[] { "Almora", "Ranikhet" }, Names("almora"));

    [Fact]
    public void CaseInsensitive()
        => Assert.Equal(Names("rishi"), Names("RISHI"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankQ_All(string? q)
        => Assert.Equal(Cities.Select(c => c.Name), Names(q));

    [Fact]
    public void NoMatch_Empty()
        => Assert.Empty(Names("zz"));

    [Theory]
    [InlineData(1, 2, true, new[] { "Almora", "Kausani" })]
    [InlineData(3, 2, false, new[] { "Rishikesh" })]
    [InlineData(2, 5, false, new string[0])]
    public void Paging_HasMore(int page, int size, bool hasMore, string[] expected)
    {
        var matches = TripCityCatalog.FilterCities(Cities, null);
        var skip = (page - 1) * size;

        Assert.Equal(expected, matches.Skip(skip).Take(size).Select(c => c.Name));
        Assert.Equal(hasMore, skip + size < matches.Count);
    }
}

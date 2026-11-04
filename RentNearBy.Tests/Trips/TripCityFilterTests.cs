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

    private static readonly List<TripCityInfo> Mixed =
    [
        new() { CityKey = 1, Name = "Dwārāhāt", DistrictName = "Almora", StateName = "Uttarakhand" },
        new() { CityKey = 2, Name = "Hēm Kund", DistrictName = "Chamoli", StateName = "Uttarakhand" },
        new() { CityKey = 3, Name = "Manali", DistrictName = "Kullu", StateName = "Himachal Pradesh", IsPopularDestination = true },
        new() { CityKey = 4, Name = "Nainital", DistrictName = "Nainital", StateName = "Uttarakhand", IsPopularDestination = true },
        new() { CityKey = 5, Name = "Shimla", DistrictName = "Shimla", StateName = "Himachal Pradesh", IsPopularDestination = true },
    ];

    private static List<string> MixedNames(string? q, bool popular = false, string? state = null)
        => TripCityCatalog.FilterCities(Mixed, q, popular, state).Select(c => c.Name).ToList();

    [Theory]
    [InlineData("dwar")]
    [InlineData("DWARAHAT")]
    [InlineData("Dwārā")]
    public void AccentedName_FoundByPlainText(string q)
        => Assert.Equal(new[] { "Dwārāhāt" }, MixedNames(q));

    [Fact]
    public void PlainName_FoundByAccentedText()
        => Assert.Equal(new[] { "Hēm Kund" }, MixedNames("hem"));

    [Fact]
    public void BlankQ_PopularFirst_Stable()
        => Assert.Equal(new[] { "Manali", "Nainital", "Shimla", "Dwārāhāt", "Hēm Kund" }, MixedNames(null));

    [Fact]
    public void PopularOnly_FiltersToPopular()
        => Assert.Equal(new[] { "Manali", "Nainital", "Shimla" }, MixedNames(null, popular: true));

    [Fact]
    public void PopularOnly_WithState()
        => Assert.Equal(new[] { "Nainital" }, MixedNames("", popular: true, state: " uttarakhand "));

    [Fact]
    public void StateFilter_HimachalPradesh()
        => Assert.Equal(new[] { "Manali", "Shimla" }, MixedNames(null, state: "Himachal Pradesh"));

    [Fact]
    public void StateFilter_UnknownState_Empty()
        => Assert.Empty(MixedNames(null, state: "Goa"));

    [Fact]
    public void Query_KeepsPrefixBeforeContains_NoPopularReorder()
        => Assert.Equal(new[] { "Manali", "Dwārāhāt", "Hēm Kund", "Shimla" }, MixedNames("m"));

    [Fact]
    public void Query_WithState_AppliesBoth()
        => Assert.Equal(new[] { "Shimla" }, MixedNames("shim", state: "Himachal Pradesh"));
}

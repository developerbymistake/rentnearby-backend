using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Districts;

public class CityNameMatcherTests
{
    private static City C(string name, double? lat = null, double? lng = null)
        => new() { Id = Guid.NewGuid(), Name = name, Latitude = (decimal?)lat, Longitude = (decimal?)lng };

    // Finding 50: Nominatim says "Bhim Tal" for a pin 0.2 km from the seeded "Bhimtal".
    [Fact]
    public void SpellingVariant_NearThePin_MatchesTheSeededCity()
    {
        var bhimtal = C("Bhimtal", 29.3460, 79.5600);
        var cities = new List<City> { C("Bhowali", 29.3850, 79.5200), bhimtal };
        Assert.Same(bhimtal, CityResolutionService.Match(cities, "Bhim Tal", 29.3475, 79.5615));
    }

    [Fact]
    public void SpellingVariant_FarFromThePin_NoMatch()
        => Assert.Null(CityResolutionService.Match([C("Bhimtal", 29.3460, 79.5600)], "Bhim Tal", 29.10, 79.50));

    [Fact]
    public void DifferentName_NearThePin_NoMatch()
        => Assert.Null(CityResolutionService.Match([C("Rampura", 29.3460, 79.5600)], "Rampur", 29.3461, 79.5601));

    [Fact]
    public void SameFoldedName_TwoVillages_NearestWins()
    {
        var north = C("Rampur", 29.60, 79.50);
        var south = C("Rampur", 29.10, 79.50);
        Assert.Same(south, CityResolutionService.Match([north, south], "Rāmpur", 29.12, 79.50));
    }

    [Fact]
    public void SameFoldedName_FarAway_StillMatches()
    {
        var seed = C("Nainital", 29.39, 79.45);
        Assert.Same(seed, CityResolutionService.Match([seed], "nainital", 29.10, 79.90));
    }

    [Fact]
    public void ExactName_WithoutCoordinates_FirstWins()
    {
        var first = C("Hēm Kund");
        var second = C("Hem Kund");
        Assert.Same(first, CityResolutionService.Match([first, second], "Hem Kund", 30.0, 79.0));
    }

    [Fact]
    public void NonLatinName_NoMatch()
        => Assert.Null(CityNameMatcher.Match([new("नैनीताल", 29.39, 79.45)], "नैनीताल", 29.39, 79.45));

    [Fact]
    public void Empty_NoMatch()
        => Assert.Null(CityNameMatcher.Match([], "Nainital", 29.39, 79.45));

    [Theory]
    [InlineData("Bhim Tal", "bhimtal")]
    [InlineData("Mc-Leod Ganj", "mcleodganj")]
    [InlineData("Dwārāhāt", "dwarahat")]
    [InlineData("", "")]
    public void CompactKey_DropsSpacesAndMarks(string name, string expected)
        => Assert.Equal(expected, CityNameMatcher.CompactKey(name));

    [Fact]
    public void IdentityKey_NonLatin_FallsBackToLowerName()
        => Assert.Equal("नैनीताल", CityNameMatcher.IdentityKey(" नैनीताल "));
}

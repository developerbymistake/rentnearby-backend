using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Districts;

public class CityNameMatchTests
{
    private static City C(string name) => new() { Id = Guid.NewGuid(), Name = name };

    [Fact]
    public void AccentlessName_MatchesAccentedSeedRow()
    {
        var seed = C("Dwārāhāt");
        Assert.Same(seed, CityResolutionService.MatchByFoldedName(new[] { C("Almora"), seed }, "Dwarahat"));
    }

    [Fact]
    public void CaseDifference_Matches()
    {
        var seed = C("Dwarahat");
        Assert.Same(seed, CityResolutionService.MatchByFoldedName(new[] { seed }, "dwarahat"));
    }

    [Fact]
    public void ExactName_Matches()
    {
        var seed = C("Nainital");
        Assert.Same(seed, CityResolutionService.MatchByFoldedName(new[] { seed }, "Nainital"));
    }

    [Fact]
    public void SpaceDifference_DoesNotMatch()
        => Assert.Null(CityResolutionService.MatchByFoldedName(new[] { C("McLeodganj") }, "Mc Leod Ganj"));

    [Fact]
    public void NonLatinName_ReturnsNull()
        => Assert.Null(CityResolutionService.MatchByFoldedName(new[] { C("Nainital"), C("नैनीताल") }, "नैनीताल"));

    [Fact]
    public void EmptyList_ReturnsNull()
        => Assert.Null(CityResolutionService.MatchByFoldedName(Array.Empty<City>(), "Nainital"));

    [Fact]
    public void FirstMatchWins()
    {
        var first = C("Hēm Kund");
        var second = C("Hem Kund");
        Assert.Same(first, CityResolutionService.MatchByFoldedName(new[] { first, second }, "Hem Kund"));
    }
}

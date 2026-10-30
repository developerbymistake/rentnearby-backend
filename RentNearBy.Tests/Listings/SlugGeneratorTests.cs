using RentNearBy.Core.Utils;

namespace RentNearBy.Tests.Listings;

public class SlugGeneratorTests
{
    [Theory]
    [InlineData("Sharma Boys & PG", "sharma-boys-pg")]
    [InlineData("The Café", "cafe")]
    [InlineData("&&&", "item")]
    [InlineData("", "item")]
    [InlineData("A/B Test", "ab-test")]
    [InlineData("  Room  ", "room")]
    [InlineData("2BHK near the Market", "2bhk-near-market")]
    [InlineData("Room for Rent and Stay", "room-rent-stay")]
    public void Generate_Name_UrlSlug(string name, string expected)
        => Assert.Equal(expected, SlugGenerator.Generate(name));

    [Fact]
    public void MakeUnique_NotTaken_ReturnsBase()
        => Assert.Equal("x", SlugGenerator.MakeUnique("x", new HashSet<string> { "y" }));

    [Fact]
    public void MakeUnique_TakenWithSuffixes_NextFreeNumber()
        => Assert.Equal("x-3", SlugGenerator.MakeUnique("x", new HashSet<string> { "x", "x-2" }));

    [Fact]
    public void MakeUnique_GapInSuffixes_FirstGap()
        => Assert.Equal("x-2", SlugGenerator.MakeUnique("x", new HashSet<string> { "x", "x-3" }));

    [Fact]
    public async Task GenerateUniqueSlugWithRetryAsync_FailsTwiceThenSaves_TrueWithThreeDistinctCandidates()
    {
        var candidates = new List<string>();
        var attempts = 0;

        var saved = await SlugGenerator.GenerateUniqueSlugWithRetryAsync("base", candidates.Add,
            () => Task.FromResult(++attempts == 3));

        Assert.True(saved);
        Assert.Equal(3, candidates.Count);
        Assert.All(candidates, c => Assert.Matches("^base-[a-z0-9]{6}$", c));
        Assert.Equal(3, candidates.Distinct().Count());
    }

    [Fact]
    public async Task GenerateUniqueSlugWithRetryAsync_AlwaysFails_FalseAfterFiveAttempts()
    {
        var candidates = new List<string>();

        var saved = await SlugGenerator.GenerateUniqueSlugWithRetryAsync("base", candidates.Add, () => Task.FromResult(false));

        Assert.False(saved);
        Assert.Equal(5, candidates.Count);
    }

    [Fact]
    public async Task GenerateUniqueSlugWithRetryAsync_CustomMaxAttempts_Honoured()
    {
        var calls = 0;

        var saved = await SlugGenerator.GenerateUniqueSlugWithRetryAsync("base", _ => { }, () =>
        {
            calls++;
            return Task.FromResult(false);
        }, maxAttempts: 2);

        Assert.False(saved);
        Assert.Equal(2, calls);
    }
}

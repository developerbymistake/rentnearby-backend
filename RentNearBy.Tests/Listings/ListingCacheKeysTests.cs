using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Listings;

public class ListingCacheKeysTests
{
    private static readonly Guid District = Guid.Parse("0d15e1c7-0000-0000-0000-00000000abcd");
    private const string D = "0d15e1c7-0000-0000-0000-00000000abcd";
    private const string N = "0d15e1c700000000000000000000abcd";

    [Theory]
    [InlineData("Taxi")]
    [InlineData("Unknown")]
    public void Keys_NoFixedKeys_Empty(string kind)
        => Assert.Empty(ListingCacheKeys.Keys(kind, District));

    [Fact]
    public void Keys_Stay_RecentOnly()
        => Assert.Equal(new[] { "home:recentStays" }, ListingCacheKeys.Keys(ListingKinds.Stay, District));

    [Theory]
    [InlineData("Unknown")]
    public void Patterns_Unknown_NoScan(string kind)
        => Assert.Empty(ListingCacheKeys.Patterns(kind, District));

    [Fact]
    public void Patterns_Stay_BrowseNearbyNearest()
        => Assert.Equal(new[] { $"stays:browse:{N}:*", $"stays:nearby:{D}:*", $"stays:nearest:{D}:*" }, ListingCacheKeys.Patterns(ListingKinds.Stay, District));

    [Fact]
    public void Patterns_Taxi_LocalOnly()
        => Assert.Equal(new[] { $"taxis:local:{N}:*" }, ListingCacheKeys.Patterns(ListingKinds.Taxi, District));

    [Fact]
    public void GlobalPatterns_Taxi_Search()
        => Assert.Equal(new[] { "taxis:search:*" }, ListingCacheKeys.GlobalPatterns(ListingKinds.Taxi));

    [Theory]
    [InlineData("Stay")]
    public void GlobalPatterns_OtherKinds_Empty(string kind)
        => Assert.Empty(ListingCacheKeys.GlobalPatterns(kind));

    [Fact]
    public void ContextPatterns_ContextOnly()
        => Assert.Equal(new[] { "context:*" }, ListingCacheKeys.ContextPatterns);

    [Fact]
    public void ContextDistrict_MatchesTheContextPattern()
    {
        var key = ListingCacheKeys.ContextDistrict(District);
        Assert.Equal($"context:district:{N}", key);
        Assert.StartsWith(ListingCacheKeys.ContextPatterns[0].TrimEnd('*'), key);
    }
}

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
    public void GenKeys_Unknown_None(string kind)
        => Assert.Empty(ListingCacheKeys.GenKeys(kind, District));

    [Fact]
    public void GenKeys_Stay_DistrictGeneration()
        => Assert.Equal(new[] { $"stays:gen:{N}" }, ListingCacheKeys.GenKeys(ListingKinds.Stay, District));

    [Fact]
    public void GenKeys_Taxi_DistrictGeneration()
        => Assert.Equal(new[] { $"taxis:gen:{N}" }, ListingCacheKeys.GenKeys(ListingKinds.Taxi, District));

    [Fact]
    public void GlobalGenKeys_Taxi_Search()
        => Assert.Equal(new[] { "taxis:search:gen" }, ListingCacheKeys.GlobalGenKeys(ListingKinds.Taxi));

    [Theory]
    [InlineData("Stay")]
    [InlineData("Unknown")]
    public void GlobalGenKeys_OtherKinds_Empty(string kind)
        => Assert.Empty(ListingCacheKeys.GlobalGenKeys(kind));

    [Fact]
    public void GenKeys_NeverMatchAFeedKey()
    {
        Assert.DoesNotContain(":gen:", RentNearBy.Api.Handlers.StayCache.BrowseKey(District, null, null, null, "newest", 10));
        Assert.DoesNotContain(":gen", RentNearBy.Api.Handlers.TaxiCache.LocalKey(District, null, null, 10));
        Assert.Equal(ListingCacheKeys.StayGen(District), RentNearBy.Api.Handlers.StayCache.GenKey(District));
        Assert.Equal(ListingCacheKeys.TaxiGen(District), RentNearBy.Api.Handlers.TaxiCache.GenKey(District));
    }

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

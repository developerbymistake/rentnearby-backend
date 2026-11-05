using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Listings;

public class ListingCacheKeysTests
{
    private static readonly Guid District = Guid.Parse("0d15e1c7-0000-0000-0000-00000000abcd");
    private static readonly Guid Other = Guid.Parse("0d15e1c7-0000-0000-0000-00000000ffff");
    private const string D = "0d15e1c7-0000-0000-0000-00000000abcd";
    private const string N = "0d15e1c700000000000000000000abcd";

    [Theory]
    [InlineData("Room")]
    [InlineData("Plot")]
    [InlineData("Taxi")]
    [InlineData("Unknown")]
    public void Keys_NoFixedKeys_Empty(string kind)
        => Assert.Empty(ListingCacheKeys.Keys(kind, District));

    [Fact]
    public void Keys_Stay_RecentOnly()
        => Assert.Equal(new[] { "home:recentStays" }, ListingCacheKeys.Keys(ListingKinds.Stay, District));

    [Theory]
    [InlineData("Room")]
    [InlineData("Plot")]
    [InlineData("Unknown")]
    public void Patterns_VersionedOrUnknown_NoScan(string kind)
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
    [InlineData("Room")]
    [InlineData("Plot")]
    [InlineData("Stay")]
    public void GlobalPatterns_OtherKinds_Empty(string kind)
        => Assert.Empty(ListingCacheKeys.GlobalPatterns(kind));

    [Fact]
    public void ContextPatterns_RoomAndPlotContext()
        => Assert.Equal(new[] { "context:*", "plot_context:*" }, ListingCacheKeys.ContextPatterns);

    [Fact]
    public void Generations_Room_EachDistrictOnceThenRecent()
        => Assert.Equal(
            new[] { $"gen:room:{N}", "gen:room:0d15e1c700000000000000000000ffff", "gen:room:recent" },
            ListingCacheKeys.Generations(ListingKinds.Room, [District, Other, District]));

    [Fact]
    public void Generations_Plot_NoDistricts_RecentOnly()
        => Assert.Equal(new[] { "gen:plot:recent" }, ListingCacheKeys.Generations(ListingKinds.Plot, []));

    [Theory]
    [InlineData("Stay")]
    [InlineData("Taxi")]
    public void Generations_UnversionedKinds_Empty(string kind)
        => Assert.Empty(ListingCacheKeys.Generations(kind, [District]));

    [Fact]
    public void DataKeys_CarryGenerationAndKeepOldPrefixes()
    {
        var cell = GeoCacheKey.Cell(29.38049, 79.45049);
        Assert.Equal($"nearby:{D}:g7:5.0:3:29380:79450", ListingCacheKeys.Nearby(ListingKinds.Room, District, 7, 5.0, cell));
        Assert.Equal($"nearby_plot:{D}:g7:5.0:3:29380:79450", ListingCacheKeys.Nearby(ListingKinds.Plot, District, 7, 5.0, cell));
        Assert.Equal($"nearest:{D}:g2:5:3:29380:79450", ListingCacheKeys.Nearest(ListingKinds.Room, District, 2, 5, cell));
        Assert.Equal($"nearest_plot:{D}:g2:5:3:29380:79450", ListingCacheKeys.Nearest(ListingKinds.Plot, District, 2, 5, cell));
        Assert.Equal($"home:browseRooms:{N}:g1:::newest:10", ListingCacheKeys.Browse(ListingKinds.Room, District, 1, null, null, "newest", 10));
        Assert.Equal($"home:forYouPlots:{D}:g3:{N}", ListingCacheKeys.ForYou(ListingKinds.Plot, District, 3, District));
        Assert.Equal("home:recentRooms:g4", ListingCacheKeys.Recent(ListingKinds.Room, 4));
    }

    [Fact]
    public void DataKey_NewGeneration_IsANewKey()
        => Assert.NotEqual(
            ListingCacheKeys.ForYou(ListingKinds.Room, District, 4, null),
            ListingCacheKeys.ForYou(ListingKinds.Room, District, 5, null));

    [Fact]
    public void ContextDistrict_MatchesTheContextPattern()
    {
        var key = ListingCacheKeys.ContextDistrict(District);
        Assert.Equal($"context:district:{N}", key);
        Assert.StartsWith(ListingCacheKeys.ContextPatterns[0].TrimEnd('*'), key);
    }
}

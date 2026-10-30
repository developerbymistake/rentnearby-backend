using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Listings;

public class ListingCacheKeysTests
{
    private static readonly Guid District = Guid.Parse("0d15e1c7-0000-0000-0000-00000000abcd");
    private const string D = "0d15e1c7-0000-0000-0000-00000000abcd";
    private const string N = "0d15e1c700000000000000000000abcd";

    [Fact]
    public void Keys_Room_RecentAndForYou()
        => Assert.Equal(new[] { "home:recentRooms", $"home:forYouRooms:{D}" }, ListingCacheKeys.Keys(ListingKinds.Room, District));

    [Fact]
    public void Keys_Plot_RecentAndForYou()
        => Assert.Equal(new[] { "home:recentPlots", $"home:forYouPlots:{D}" }, ListingCacheKeys.Keys(ListingKinds.Plot, District));

    [Fact]
    public void Keys_Stay_RecentOnly()
        => Assert.Equal(new[] { "home:recentStays" }, ListingCacheKeys.Keys(ListingKinds.Stay, District));

    [Theory]
    [InlineData("Taxi")]
    [InlineData("Unknown")]
    public void Keys_NoFixedKeys_Empty(string kind)
        => Assert.Empty(ListingCacheKeys.Keys(kind, District));

    [Fact]
    public void Patterns_Room_NearbyNearestAndBrowse()
        => Assert.Equal(new[] { $"nearby:{D}:*", $"nearest:{D}:*", $"home:browseRooms:{N}:*" }, ListingCacheKeys.Patterns(ListingKinds.Room, District));

    [Fact]
    public void Patterns_Plot_NearbyNearestAndBrowse()
        => Assert.Equal(new[] { $"nearby_plot:{D}:*", $"nearest_plot:{D}:*", $"home:browsePlots:{N}:*" }, ListingCacheKeys.Patterns(ListingKinds.Plot, District));

    [Fact]
    public void Patterns_Stay_BrowseNearbyNearest()
        => Assert.Equal(new[] { $"stays:browse:{N}:*", $"stays:nearby:{D}:*", $"stays:nearest:{D}:*" }, ListingCacheKeys.Patterns(ListingKinds.Stay, District));

    [Fact]
    public void Patterns_Taxi_LocalOnly()
        => Assert.Equal(new[] { $"taxis:local:{N}:*" }, ListingCacheKeys.Patterns(ListingKinds.Taxi, District));

    [Fact]
    public void Patterns_Unknown_Empty()
        => Assert.Empty(ListingCacheKeys.Patterns("Unknown", District));

    [Fact]
    public void GlobalPatterns_Taxi_Search()
        => Assert.Equal(new[] { "taxis:search:*" }, ListingCacheKeys.GlobalPatterns(ListingKinds.Taxi));

    [Theory]
    [InlineData("Room")]
    [InlineData("Plot")]
    [InlineData("Stay")]
    public void GlobalPatterns_OtherKinds_Empty(string kind)
        => Assert.Empty(ListingCacheKeys.GlobalPatterns(kind));
}

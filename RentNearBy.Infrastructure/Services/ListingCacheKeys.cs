using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

// Redis key patterns for every public feed a plan rank or visibility change can touch. Formats match the
// key builders in the handlers (ListingsHandlers, PlotHandlers, HomeHandlers, StayCache, TaxiCache).
public static class ListingCacheKeys
{
    public const string RecentRooms = "home:recentRooms";
    public const string RecentPlots = "home:recentPlots";
    public const string RecentStays = "home:recentStays";

    // Fixed keys to delete directly (no scan).
    public static string[] Keys(string kind, Guid districtId) => kind switch
    {
        ListingKinds.Room => [RecentRooms, $"home:forYouRooms:{districtId}"],
        ListingKinds.Plot => [RecentPlots, $"home:forYouPlots:{districtId}"],
        ListingKinds.Stay => [RecentStays],
        _ => [],
    };

    // Patterns scanned once per district.
    public static string[] Patterns(string kind, Guid districtId) => kind switch
    {
        ListingKinds.Room =>
        [
            $"nearby:{districtId}:*",
            $"nearest:{districtId}:*",
            $"home:browseRooms:{districtId:N}:*",
        ],
        ListingKinds.Plot =>
        [
            $"nearby_plot:{districtId}:*",
            $"nearest_plot:{districtId}:*",
            $"home:browsePlots:{districtId:N}:*",
        ],
        ListingKinds.Stay =>
        [
            $"stays:browse:{districtId:N}:*",
            $"stays:nearby:{districtId}:*",
            $"stays:nearest:{districtId}:*",
        ],
        ListingKinds.Taxi => [$"taxis:local:{districtId:N}:*"],
        _ => [],
    };

    // District-free patterns, scanned once per call.
    public static string[] GlobalPatterns(string kind) => kind switch
    {
        ListingKinds.Taxi => ["taxis:search:*"],
        _ => [],
    };
}

using System.Globalization;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

// Redis keys for every public feed a plan rank or visibility change can touch.
// Rooms and plots are versioned: each district (and the district-free recent feed) has a generation counter;
// readers put the current value in the data key, a bust is one INCR, old keys die by TTL. No SCAN, and a slow
// reader that read the DB before a bust writes under a generation nobody reads again.
// Stays and taxis keep fixed keys + prefix patterns (key builders in StayCache, TaxiCache).
public static class ListingCacheKeys
{
    public const string RecentStays = "home:recentStays";

    public static bool IsVersioned(string kind) => kind is ListingKinds.Room or ListingKinds.Plot;

    public static string DistrictGen(string kind, Guid districtId) => $"gen:{kind.ToLowerInvariant()}:{districtId:N}";
    public static string RecentGen(string kind) => $"gen:{kind.ToLowerInvariant()}:recent";

    // Generation counters one bust of (kind, districts) increments.
    public static string[] Generations(string kind, IEnumerable<Guid> districtIds)
        => IsVersioned(kind)
            ? districtIds.Distinct().Select(d => DistrictGen(kind, d)).Append(RecentGen(kind)).ToArray()
            : [];

    // Old prefixes are kept (nearby:{d}:, nearest:{d}:, ...) so any remaining prefix scan still finds the keys.
    public static string Nearby(string kind, Guid districtId, long gen, double radiusKey, GeoCell cell)
        => string.Create(CultureInfo.InvariantCulture, $"{(kind == ListingKinds.Plot ? "nearby_plot" : "nearby")}:{districtId}:g{gen}:{radiusKey:F1}:{cell.Key}");

    public static string Nearest(string kind, Guid districtId, long gen, int count, GeoCell cell)
        => $"{(kind == ListingKinds.Plot ? "nearest_plot" : "nearest")}:{districtId}:g{gen}:{count}:{cell.Key}";

    public static string Browse(string kind, Guid districtId, long gen, Guid? cityId, Guid? typeId, string sort, int pageSize)
        => $"{(kind == ListingKinds.Plot ? "home:browsePlots" : "home:browseRooms")}:{districtId:N}:g{gen}:{cityId:N}:{typeId:N}:{sort}:{pageSize}";

    public static string ForYou(string kind, Guid districtId, long gen, Guid? cityId)
        => $"{(kind == ListingKinds.Plot ? "home:forYouPlots" : "home:forYouRooms")}:{districtId}:g{gen}:{cityId:N}";

    public static string Recent(string kind, long gen)
        => $"{(kind == ListingKinds.Plot ? "home:recentPlots" : "home:recentRooms")}:g{gen}";

    // District context payload (district fields + its cities), the only cell-invariant part of /context.
    public static string ContextDistrict(Guid districtId) => $"context:district:{districtId:N}";

    // Fixed keys to delete directly (no scan).
    public static string[] Keys(string kind, Guid districtId) => kind switch
    {
        ListingKinds.Stay => [RecentStays],
        _ => [],
    };

    // Patterns scanned once per district (unversioned kinds only).
    public static string[] Patterns(string kind, Guid districtId) => kind switch
    {
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

    // Redis patterns holding the /context DTOs (district id, name, state, businessOnboardingEnabled).
    public static readonly string[] ContextPatterns = ["context:*", "plot_context:*"];
}

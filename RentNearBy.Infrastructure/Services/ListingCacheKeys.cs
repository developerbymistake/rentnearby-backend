using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

// Redis keys for every public feed a plan rank or visibility change can touch.
// Stays and taxis use fixed keys + prefix patterns (key builders in StayCache, TaxiCache).
public static class ListingCacheKeys
{
    public const string RecentStays = "home:recentStays";

    // District context payload (district fields + its cities), the only cell-invariant part of /context.
    public static string ContextDistrict(Guid districtId) => $"context:district:{districtId:N}";

    // Fixed keys to delete directly (no scan).
    public static string[] Keys(string kind, Guid districtId) => kind switch
    {
        ListingKinds.Stay => [RecentStays],
        _ => [],
    };

    // Patterns scanned once per district.
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
    public static readonly string[] ContextPatterns = ["context:*"];
}

using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

// Redis keys for every public feed a plan rank or visibility change can touch.
// Stays and taxis: fixed keys deleted directly + generation keys bumped with INCR (FeedCache, CacheStamp); the feed key
// builders are in StayCache and TaxiCache. No stay or taxi invalidation scans the keyspace.
public static class ListingCacheKeys
{
    public const string RecentStays = "home:recentStays";

    // Stay browse/nearby/nearest of a district; taxi local of a district; taxi route search (district-free).
    public static string StayGen(Guid districtId) => $"stays:gen:{districtId:N}";
    public static string TaxiGen(Guid districtId) => $"taxis:gen:{districtId:N}";
    public const string TaxiSearchGen = "taxis:search:gen";

    // Stay nearby/nearest without a district (distance only): one generation for every stay change anywhere.
    public const string StayAllGen = "stays:gen:all";

    // District context payload (district fields + its cities), the only cell-invariant part of /context.
    public static string ContextDistrict(Guid districtId) => $"context:district:{districtId:N}";

    // Fixed keys to delete directly (no scan).
    public static string[] Keys(string kind, Guid districtId) => kind switch
    {
        ListingKinds.Stay => [RecentStays],
        _ => [],
    };

    // Generation keys bumped once per district.
    public static string[] GenKeys(string kind, Guid districtId) => kind switch
    {
        ListingKinds.Stay => [StayGen(districtId)],
        ListingKinds.Taxi => [TaxiGen(districtId)],
        _ => [],
    };

    // District-free generation keys, bumped once per call.
    public static string[] GlobalGenKeys(string kind) => kind switch
    {
        ListingKinds.Taxi => [TaxiSearchGen],
        ListingKinds.Stay => [StayAllGen],
        _ => [],
    };

    // Redis patterns holding the /context DTOs (district id, name, state, businessOnboardingEnabled).
    public static readonly string[] ContextPatterns = ["context:*"];
}

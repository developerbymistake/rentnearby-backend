using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Public, non-admin config the client apps need before any specific action — currently just the
// listing-creation caps, read by the consumer app's Add Room/Add Plot gating and by the admin app
// for parity. Anonymous by design: this is read-only reference data, not user-specific.
public static class ConfigHandlers
{
    public const string ListingLimitsCacheKey = "config_listing_limits";
    public const string PaymentFeatureCacheKey = "config_payment_feature";
    public const string AppTabsCacheKey = AppTabCache.Key;
    public const string RolesCacheKey = "config_roles";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public static async Task<IResult> GetListingLimits(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue(ListingLimitsCacheKey, out (int RoomLimit, int PlotLimit) cached))
        {
            var settings = await unitOfWork.ListingLimitSettings.GetAllAsync();
            var roomLimit = settings.FirstOrDefault(s => s.ListingKind == ListingKinds.Room)?.MaxListings ?? 5;
            var plotLimit = settings.FirstOrDefault(s => s.ListingKind == ListingKinds.Plot)?.MaxListings ?? 5;
            cached = (roomLimit, plotLimit);
            cache.Set(ListingLimitsCacheKey, cached, CacheTtl);
        }

        return OkResponse(new { roomLimit = cached.RoomLimit, plotLimit = cached.PlotLimit });
    }

    // Fallback when the row is missing/null is Enabled = true — fail toward REQUIRING payment. This is
    // deliberately the OPPOSITE polarity from the seed's IsEnabled = false default: a missing row must
    // never silently make Go-Live free for everyone. Do not "simplify" this to match the seed default.
    public static async Task<(bool Enabled, int FreeDurationDays)> GetPaymentFeatureCachedAsync(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue(PaymentFeatureCacheKey, out (bool Enabled, int FreeDurationDays) cached))
        {
            var flag = await unitOfWork.AppFeatureFlags.GetByKeyAsync(AppFeatureKeys.PaymentEnabled);
            cached = (flag?.IsEnabled ?? true, flag?.FreeDurationDays ?? 30);
            cache.Set(PaymentFeatureCacheKey, cached, CacheTtl);
        }

        return cached;
    }

    public static async Task<IResult> GetPaymentFeature(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var (enabled, freeDurationDays) = await GetPaymentFeatureCachedAsync(unitOfWork, cache);
        return OkResponse(new { enabled, freeGoLiveDurationDays = freeDurationDays });
    }

    // Master table for the bottom-nav tabs (RentNearBy.Core.Models.AppTabKeys), read through AppTabCache so
    // TabGateFilter, Home, roles, chat and the digest job all share one entry. AdminHandlers.UpdateAppTab
    // rewrites it on every change.
    public static Task<List<AppTabDto>> GetAppTabsCachedAsync(IUnitOfWork unitOfWork, IMemoryCache cache)
        => AppTabCache.GetAsync(unitOfWork, cache);

    // Active Role rows only (tab-independent); availability is filtered per call from the tab snapshot, so a
    // tab flip needs no eviction here.
    public static async Task<IReadOnlyList<Role>> GetActiveRolesCachedAsync(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue(RolesCacheKey, out IReadOnlyList<Role>? cached) || cached == null)
        {
            cached = await unitOfWork.Roles.GetActiveOrderedAsync();
            cache.Set(RolesCacheKey, cached, CacheTtl);
        }
        return cached;
    }

    public static async Task<List<Role>> GetAvailableRolesAsync(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var tabs = await GetAppTabsCachedAsync(unitOfWork, cache);
        var roles = await GetActiveRolesCachedAsync(unitOfWork, cache);
        return roles.Where(r => TabState.IsRoleAvailable(tabs, r)).ToList();
    }

    public static async Task<IResult> GetAppTabs(IUnitOfWork unitOfWork, IMemoryCache cache)
        => OkResponse(await GetAppTabsCachedAsync(unitOfWork, cache));

    public static string AppVersionCacheKey(string normalizedPlatform) => $"config_app_version_{normalizedPlatform}";
    private static readonly TimeSpan AppVersionCacheTtl = TimeSpan.FromMinutes(2);

    private sealed class AppVersionCacheEntry
    {
        public required AppVersionConfigDto Dto { get; init; }
        public required bool RowExists { get; init; }
        public required int MaxAcceptedReportedBuild { get; init; }
        public int MaxSeenBuild;
    }

    // Missing row => enabled:false (fail-open), so a client holding a cached FORCE clears it.
    private static async Task<AppVersionCacheEntry> GetAppVersionCachedAsync(string platform, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var key = AppVersionCacheKey(platform);
        if (cache.TryGetValue(key, out AppVersionCacheEntry? cached) && cached != null) return cached;

        var row = await unitOfWork.AppVersionConfigs.GetByPlatformAsync(platform);
        var entry = row == null
            ? new AppVersionCacheEntry
            {
                Dto = new AppVersionConfigDto
                {
                    Platform = platform,
                    Enabled = false,
                    ForceCacheMaxAgeHours = AppVersionRules.DefaultForceCacheMaxAgeHours,
                },
                RowExists = false,
                MaxAcceptedReportedBuild = 0,
            }
            : new AppVersionCacheEntry
            {
                Dto = new AppVersionConfigDto
                {
                    Platform = row.Platform,
                    Enabled = row.IsEnabled,
                    MinBuildNumber = row.MinBuildNumber,
                    MinVersionName = row.MinVersionName,
                    LatestBuildNumber = row.LatestBuildNumber,
                    LatestVersionName = row.LatestVersionName,
                    StoreUrl = row.StoreUrl,
                    Message = row.Message,
                    ForceCacheMaxAgeHours = row.ForceCacheMaxAgeHours,
                },
                RowExists = true,
                MaxAcceptedReportedBuild = Math.Max(row.LatestBuildNumber, AppVersionRules.FirstGatedBuild) + AppVersionRules.MaxSeenBuildHeadroom,
                MaxSeenBuild = row.MaxSeenBuild,
            };

        cache.Set(key, entry, AppVersionCacheTtl);
        return entry;
    }

    public static async Task<IResult> GetAppVersion(
        string? platform, string? build, HttpContext httpContext,
        IUnitOfWork unitOfWork, IMemoryCache cache, ILoggerFactory loggerFactory)
    {
        if (!AppPlatforms.TryNormalize(platform, out var key))
            return BadRequestResponse("platform must be 'android' or 'ios'");

        var entry = await GetAppVersionCachedAsync(key, unitOfWork, cache);

        if (entry.RowExists
            && int.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out var reported)
            && reported >= AppVersionRules.MinBuildNumber
            && reported <= entry.MaxAcceptedReportedBuild)
        {
            await RecordSeenBuildAsync(entry, key, reported, unitOfWork, cache, loggerFactory);
        }

        httpContext.Response.Headers.CacheControl = "no-cache";
        return OkResponse(entry.Dto);
    }

    private static async Task RecordSeenBuildAsync(
        AppVersionCacheEntry entry, string platform, int reported,
        IUnitOfWork unitOfWork, IMemoryCache cache, ILoggerFactory loggerFactory)
    {
        var seen = Volatile.Read(ref entry.MaxSeenBuild);
        while (reported > seen)
        {
            var previous = Interlocked.CompareExchange(ref entry.MaxSeenBuild, reported, seen);
            if (previous != seen)
            {
                seen = previous;
                continue;
            }

            try
            {
                await unitOfWork.AppVersionConfigs.TryRaiseMaxSeenBuildAsync(platform, reported);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger(nameof(ConfigHandlers))
                    .LogWarning(ex, "Failed to persist MaxSeenBuild {Build} for {Platform}", reported, platform);
                cache.Remove(AppVersionCacheKey(platform));
            }
            return;
        }
    }
}

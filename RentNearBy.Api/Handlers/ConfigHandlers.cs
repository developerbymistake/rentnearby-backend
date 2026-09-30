using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Public, non-admin config the client apps need before any specific action — currently just the
// listing-creation caps, read by the consumer app's Add Room/Add Plot gating and by the admin app
// for parity. Anonymous by design: this is read-only reference data, not user-specific.
public static class ConfigHandlers
{
    public const string ListingLimitsCacheKey = "config_listing_limits";
    public const string PaymentFeatureCacheKey = "config_payment_feature";
    public const string AppTabsCacheKey = "config_app_tabs";
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

    // Master table for the bottom-nav tabs (RentNearBy.Core.Models.AppTabKeys). Cached as one list
    // (only a handful of rows, never paginated) so both the public endpoint and TabGateFilter (Filters/TabGateFilter.cs,
    // which blocks every request under a deactivated vertical's route group) share one cache entry —
    // AdminHandlers.UpdateAppTab evicts this key on every change.
    public static async Task<List<AppTabDto>> GetAppTabsCachedAsync(IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue(AppTabsCacheKey, out List<AppTabDto>? cached) || cached == null)
        {
            var tabs = await unitOfWork.AppTabs.GetAllAsync();
            cached = tabs.OrderBy(t => t.SortOrder)
                .Select(t => new AppTabDto { TabKey = t.TabKey, DisplayName = t.DisplayName, IsActive = t.IsActive, SortOrder = t.SortOrder })
                .ToList();
            cache.Set(AppTabsCacheKey, cached, CacheTtl);
        }
        return cached;
    }

    // Fail-open: a tab key with no seeded row (shouldn't happen post-seed, but never let a missing row
    // silently take down a whole vertical's API surface) is treated as active.
    public static async Task<bool> IsTabActiveCachedAsync(string tabKey, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        var tabs = await GetAppTabsCachedAsync(unitOfWork, cache);
        return tabs.FirstOrDefault(t => t.TabKey == tabKey)?.IsActive ?? true;
    }

    public static async Task<IResult> GetAppTabs(IUnitOfWork unitOfWork, IMemoryCache cache)
        => OkResponse(await GetAppTabsCachedAsync(unitOfWork, cache));
}

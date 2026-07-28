using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using static RentNearBy.Api.Extensions.ApiResults;
using System.Text.Json;

namespace RentNearBy.Api.Handlers;

// Public, non-admin config the client apps need before any specific action — currently just the
// listing-creation caps, read by the consumer app's Add Room/Add Plot gating and by the admin app
// for parity. Anonymous by design: this is read-only reference data, not user-specific.
public static class ConfigHandlers
{
    public const string ListingLimitsCacheKey = "config_listing_limits";
    public const string PaymentFeatureCacheKey = "config_payment_feature";
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

    // Service Itinerary disclaimer text — one AppSetting row (AppSettingTypes.ItineraryDisclaimer)
    // holding both variants as JSON ({"hillRegionText":"...","generalText":"..."}); which variant is
    // shown depends on the Service's own TerrainType, not on any per-request input.
    public const string ItineraryDisclaimerCacheKey = "config_itinerary_disclaimer";

    public static async Task<string?> ResolveItineraryDisclaimerAsync(string? terrainType, IUnitOfWork unitOfWork, IMemoryCache cache)
    {
        if (!cache.TryGetValue(ItineraryDisclaimerCacheKey, out (string HillRegionText, string GeneralText) cached))
        {
            var setting = await unitOfWork.AppSettings.GetByTypeAsync(AppSettingTypes.ItineraryDisclaimer);
            if (setting == null) return null;

            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(setting.Value) ?? new();
            cached = (parsed.GetValueOrDefault("hillRegionText", ""), parsed.GetValueOrDefault("generalText", ""));
            cache.Set(ItineraryDisclaimerCacheKey, cached, CacheTtl);
        }

        return terrainType == "Hill" ? cached.HillRegionText : cached.GeneralText;
    }
}

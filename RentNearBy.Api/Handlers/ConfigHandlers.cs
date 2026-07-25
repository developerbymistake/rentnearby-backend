using Microsoft.Extensions.Caching.Memory;
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
}

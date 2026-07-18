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
}

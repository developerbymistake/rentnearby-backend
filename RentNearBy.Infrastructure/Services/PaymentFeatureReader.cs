using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Missing row/null = Enabled true (fail toward requiring payment), FreeDays 30. This is deliberately the
// opposite polarity from the seed's IsEnabled = false; do not "simplify" it to match the seed.
public class PaymentFeatureReader(ApplicationDbContext db, IMemoryCache cache) : IPaymentFeatureReader
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private const int DefaultFreeDays = 30;

    public async Task<PaymentFeatureState> GetAsync()
    {
        if (cache.TryGetValue(IPaymentFeatureReader.CacheKey, out PaymentFeatureState? cached) && cached != null)
            return cached;

        var flag = await db.AppFeatureFlags.AsNoTracking()
            .FirstOrDefaultAsync(f => f.FeatureKey == AppFeatureKeys.PaymentEnabled);
        var state = new PaymentFeatureState(
            flag?.IsEnabled ?? true,
            flag?.FreeDurationDays ?? DefaultFreeDays,
            flag?.FreeModeListingLimit);
        cache.Set(IPaymentFeatureReader.CacheKey, state, Ttl);
        return state;
    }

    public void Invalidate() => cache.Remove(IPaymentFeatureReader.CacheKey);
}

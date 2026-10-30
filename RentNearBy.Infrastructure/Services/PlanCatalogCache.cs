using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// The one reader of Plans/PlanFeatures/PlanPrices for hot paths. Process-local IMemoryCache, same
// version guard as AppTabCache: a read that started before an admin write never stores its snapshot.
// Single api instance only; a scale-out needs a shared version key.
public class PlanCatalogCache(ApplicationDbContext db, IMemoryCache cache) : IPlanCatalogCache
{
    public const string Key = "plan_catalog";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private static int _version;
    private static volatile PlanCatalogSnapshot? _last;

    // Last snapshot any request built; lets repositories read the rank list without DI. Null until first load.
    public static PlanCatalogSnapshot? LastSnapshot => _last;

    public async Task<PlanCatalogSnapshot> GetAsync()
    {
        if (cache.TryGetValue(Key, out PlanCatalogSnapshot? cached) && cached != null) return cached;
        return await RefreshAsync();
    }

    public async Task<PlanCatalogSnapshot> RefreshAsync()
    {
        var version = Volatile.Read(ref _version);
        var snapshot = await LoadAsync(db);
        if (version == Volatile.Read(ref _version))
        {
            cache.Set(Key, snapshot, Ttl);
            _last = snapshot;
        }
        return snapshot;
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
        cache.Remove(Key);
    }

    internal static async Task<PlanCatalogSnapshot> LoadAsync(ApplicationDbContext db)
    {
        var plans = await db.Plans.AsNoTracking().ToListAsync();
        var features = await db.PlanFeatures.AsNoTracking().ToListAsync();
        var prices = await db.PlanPrices.AsNoTracking().ToListAsync();

        var featuresByPlan = features.GroupBy(f => f.PlanId)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, int>)g.ToDictionary(f => f.Key, f => f.Value));
        var pricesByPlan = prices.GroupBy(p => p.PlanId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CatalogPrice>)g
                .OrderBy(p => p.CycleMonths)
                .Select(p => new CatalogPrice(p.Id, p.CycleMonths, p.PriceInr, p.RazorpayPlanId, p.IsEnabled,
                    p.PlayBasePlanId, p.PlayTrialOfferId))
                .ToList());

        return new PlanCatalogSnapshot(plans.Select(p =>
        {
            var f = featuresByPlan.GetValueOrDefault(p.Id) ?? new Dictionary<string, int>();
            return new CatalogPlan(
                p.Id, p.FeatureKey, p.Code, p.Name, p.Tagline, p.IsDefault, p.IsEnabled, p.TrialDays, p.SortOrder,
                f, PlanFeatureValues.From(f), pricesByPlan.GetValueOrDefault(p.Id) ?? Array.Empty<CatalogPrice>(),
                p.PlayProductId);
        }));
    }

    // Rank phases for browse: the cached snapshot when loaded, else one small PlanFeatures read.
    internal static async Task<IReadOnlyList<short>> RanksForAsync(ApplicationDbContext db, string featureKey)
    {
        var snap = _last;
        if (snap != null) return snap.DistinctPriorityRanks(featureKey);
        var loaded = await LoadAsync(db);
        _last ??= loaded;
        return loaded.DistinctPriorityRanks(featureKey);
    }
}

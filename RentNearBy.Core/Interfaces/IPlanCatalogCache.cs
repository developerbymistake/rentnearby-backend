using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public sealed record CatalogPrice(Guid Id, short CycleMonths, int PriceInr, string? RazorpayPlanId, bool IsEnabled);

// Typed view of a plan's PlanFeatures; a missing key reads as 0.
public sealed record PlanFeatureValues(int ListingLimit, int Priority, bool Featured, int JobsPosts)
{
    public static PlanFeatureValues From(IReadOnlyDictionary<string, int> features) => new(
        features.GetValueOrDefault(PlanFeatureKeys.ListingLimit),
        features.GetValueOrDefault(PlanFeatureKeys.Priority),
        features.GetValueOrDefault(PlanFeatureKeys.Featured) > 0,
        features.GetValueOrDefault(PlanFeatureKeys.JobsPosts));

    public static PlanFeatureValues Max(PlanFeatureValues a, PlanFeatureValues b) => new(
        Math.Max(a.ListingLimit, b.ListingLimit),
        Math.Max(a.Priority, b.Priority),
        a.Featured || b.Featured,
        Math.Max(a.JobsPosts, b.JobsPosts));

    // Used only if a feature has no default plan row (unseeded database).
    public static readonly PlanFeatureValues Fallback = new(1, 0, false, 1);
}

public sealed record CatalogPlan(
    Guid Id,
    string FeatureKey,
    string Code,
    string Name,
    string? Tagline,
    bool IsDefault,
    bool IsEnabled,
    int TrialDays,
    int SortOrder,
    IReadOnlyDictionary<string, int> Features,
    PlanFeatureValues Values,
    IReadOnlyList<CatalogPrice> Prices);

public sealed class PlanCatalogSnapshot
{
    private static readonly IReadOnlyList<CatalogPlan> NoPlans = [];
    private static readonly IReadOnlyList<short> ZeroRank = [0];

    private readonly Dictionary<Guid, CatalogPlan> _byId;
    private readonly Dictionary<string, IReadOnlyList<short>> _ranks;

    public PlanCatalogSnapshot(IEnumerable<CatalogPlan> plans)
    {
        var list = plans.OrderBy(p => p.FeatureKey).ThenBy(p => p.SortOrder).ThenBy(p => p.Name).ToList();
        _byId = list.ToDictionary(p => p.Id);
        PlansByFeature = list.GroupBy(p => p.FeatureKey)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CatalogPlan>)g.ToList());
        // Disabled plans are included: their holders keep their rank until expiry.
        _ranks = PlansByFeature.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<short>)kv.Value.Select(p => (short)Math.Clamp(p.Values.Priority, 0, short.MaxValue))
                .Append((short)0).Distinct().OrderByDescending(r => r).ToList());
    }

    public IReadOnlyDictionary<string, IReadOnlyList<CatalogPlan>> PlansByFeature { get; }

    public CatalogPlan? GetPlan(Guid planId) => _byId.GetValueOrDefault(planId);

    public IReadOnlyList<CatalogPlan> PlansOf(string featureKey) => PlansByFeature.GetValueOrDefault(featureKey) ?? NoPlans;

    public IReadOnlyDictionary<string, int> FeaturesOf(Guid planId)
        => _byId.TryGetValue(planId, out var p) ? p.Features : new Dictionary<string, int>();

    public IReadOnlyList<CatalogPrice> PricesOf(Guid planId)
        => _byId.TryGetValue(planId, out var p) ? p.Prices : Array.Empty<CatalogPrice>();

    public CatalogPlan? DefaultPlan(string featureKey) => PlansOf(featureKey).FirstOrDefault(p => p.IsDefault);

    public PlanFeatureValues DefaultValues(string featureKey) => DefaultPlan(featureKey)?.Values ?? PlanFeatureValues.Fallback;

    // Always contains 0, sorted high to low.
    public IReadOnlyList<short> DistinctPriorityRanks(string featureKey) => _ranks.GetValueOrDefault(featureKey) ?? ZeroRank;
}

// The only hot-path reader of Plans/PlanFeatures/PlanPrices (IMemoryCache, version-guarded like AppTabCache).
public interface IPlanCatalogCache
{
    Task<PlanCatalogSnapshot> GetAsync();
    Task<PlanCatalogSnapshot> RefreshAsync();
    // Call before an admin catalog write commits; RefreshAsync after it.
    void Invalidate();
}

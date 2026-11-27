using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Core.Interfaces;

public sealed record CatalogPrice(Guid Id, short CycleMonths, int PriceInr, string? RazorpayPlanId, bool IsEnabled,
    string? PlayBasePlanId, string? PlayTrialOfferId);

// Typed view of a plan's PlanFeatures. A missing legacy key (the first four) reads as 0; a missing newer key reads as its
// Free value, which is also the parameter default, so a plan saved before a key existed never drops below Free.
public sealed record PlanFeatureValues(
    int ListingLimit,
    int Priority,
    bool Featured,
    int JobsPosts,
    int Photos = 2,
    int RoomTypes = 2,
    int Routes = 1,
    int RingWeight = 1,
    int CalendarEntries = 4,
    int BlockedRanges = 1,
    int UpcomingDays = 15,
    int MonthsAhead = 0,
    bool MonthSummary = false,
    bool Reminders = false,
    int SpecialPrices = 1,
    bool DemandCount = false,
    int StayQuotationsPerMonth = 2)
{
    // Used only if a feature has no default plan row (unseeded database).
    public static readonly PlanFeatureValues Fallback = new(1, 0, false, 1);

    public int Get(string key) => key switch
    {
        PlanFeatureKeys.ListingLimit => ListingLimit,
        PlanFeatureKeys.Priority => Priority,
        PlanFeatureKeys.Featured => Featured ? 1 : 0,
        PlanFeatureKeys.JobsPosts => JobsPosts,
        PlanFeatureKeys.Photos => Photos,
        PlanFeatureKeys.RoomTypes => RoomTypes,
        PlanFeatureKeys.Routes => Routes,
        PlanFeatureKeys.RingWeight => RingWeight,
        PlanFeatureKeys.CalendarEntries => CalendarEntries,
        PlanFeatureKeys.BlockedRanges => BlockedRanges,
        PlanFeatureKeys.UpcomingDays => UpcomingDays,
        PlanFeatureKeys.MonthsAhead => MonthsAhead,
        PlanFeatureKeys.MonthSummary => MonthSummary ? 1 : 0,
        PlanFeatureKeys.Reminders => Reminders ? 1 : 0,
        PlanFeatureKeys.SpecialPrices => SpecialPrices,
        PlanFeatureKeys.DemandCount => DemandCount ? 1 : 0,
        PlanFeatureKeys.StayQuotationsPerMonth => StayQuotationsPerMonth,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown plan feature key"),
    };

    public static PlanFeatureValues Build(Func<string, int> read) => new(
        read(PlanFeatureKeys.ListingLimit),
        read(PlanFeatureKeys.Priority),
        read(PlanFeatureKeys.Featured) > 0,
        read(PlanFeatureKeys.JobsPosts),
        read(PlanFeatureKeys.Photos),
        read(PlanFeatureKeys.RoomTypes),
        read(PlanFeatureKeys.Routes),
        read(PlanFeatureKeys.RingWeight),
        read(PlanFeatureKeys.CalendarEntries),
        read(PlanFeatureKeys.BlockedRanges),
        read(PlanFeatureKeys.UpcomingDays),
        read(PlanFeatureKeys.MonthsAhead),
        read(PlanFeatureKeys.MonthSummary) > 0,
        read(PlanFeatureKeys.Reminders) > 0,
        read(PlanFeatureKeys.SpecialPrices),
        read(PlanFeatureKeys.DemandCount) > 0,
        read(PlanFeatureKeys.StayQuotationsPerMonth));

    public static PlanFeatureValues From(IReadOnlyDictionary<string, int> features) => Build(k =>
        features.TryGetValue(k, out var v) ? v : PlanFeatureKeys.Legacy.Contains(k) ? 0 : Fallback.Get(k));

    public static PlanFeatureValues Max(PlanFeatureValues a, PlanFeatureValues b) => Build(k => Math.Max(a.Get(k), b.Get(k)));

    // Every key takes the higher value except the Ranking keys (Priority, Featured, RingWeight), which stay as in `off`.
    public static PlanFeatureValues MaxExceptRanking(PlanFeatureValues off, PlanFeatureValues active)
        => Build(k => PlanKeyCatalog.Of(k).Kind == PlanKeyKind.Ranking ? off.Get(k) : Math.Max(off.Get(k), active.Get(k)));

    // The one payment rule (contract 3.1, D-24 / FB-14). ON: the plan's own values. OFF: `noPlan` = OffValues; an admin grant
    // keeps its own values on every key (ranking too); any other active row (Play subscription, trial, prepaid) keeps Plus
    // limits but gets Free ranking like everyone else.
    public static PlanFeatureValues Effective(bool paymentEnabled, PlanFeatureValues noPlan, PlanFeatureValues plan, bool adminGrant)
        => paymentEnabled ? plan : adminGrant ? Max(noPlan, plan) : MaxExceptRanking(noPlan, plan);

    // Payment OFF: every Limit key takes the highest value over the enabled plans of the kind (default included),
    // every Ranking and Stored key the default plan's value. Nobody is ranked above anybody while nothing can be bought.
    public static PlanFeatureValues OffValues(PlanCatalogSnapshot snapshot, string featureKey)
    {
        var defaults = snapshot.DefaultValues(featureKey);
        var enabled = snapshot.PlansOf(featureKey).Where(p => p.IsEnabled || p.IsDefault).Select(p => p.Values).Append(defaults).ToList();
        return Build(k => PlanKeyCatalog.Of(k).Kind == PlanKeyKind.Limit ? enabled.Max(v => v.Get(k)) : defaults.Get(k));
    }
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
    IReadOnlyList<CatalogPrice> Prices,
    string? PlayProductId);

public sealed class PlanCatalogSnapshot
{
    private static readonly IReadOnlyList<CatalogPlan> NoPlans = [];
    private static readonly IReadOnlyList<short> ZeroRank = [0];

    private readonly Dictionary<Guid, CatalogPlan> _byId;
    private readonly Dictionary<string, CatalogPlan> _byPlayProduct;
    private readonly Dictionary<string, IReadOnlyList<short>> _ranks;

    public PlanCatalogSnapshot(IEnumerable<CatalogPlan> plans)
    {
        var list = plans.OrderBy(p => p.FeatureKey).ThenBy(p => p.SortOrder).ThenBy(p => p.Name).ToList();
        _byId = list.ToDictionary(p => p.Id);
        _byPlayProduct = list.Where(p => p.PlayProductId != null)
            .GroupBy(p => p.PlayProductId!)
            .ToDictionary(g => g.Key, g => g.First());
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

    // Disabled plans included: an existing subscription of a disabled plan still maps.
    public CatalogPlan? FindByPlayProduct(string productId) => _byPlayProduct.GetValueOrDefault(productId);

    public CatalogPrice? FindPrice(Guid planId, string basePlanId)
        => PricesOf(planId).FirstOrDefault(p => p.PlayBasePlanId == basePlanId);

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

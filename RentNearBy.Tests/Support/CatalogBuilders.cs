namespace RentNearBy.Tests.Support;

public sealed class CatalogPriceBuilder
{
    private Guid _id = TestIds.Price1m;
    private short _cycleMonths = 1;
    private int _priceInr = 299;
    private string? _razorpayPlanId;
    private bool _isEnabled = true;
    private string? _playBasePlanId = "cycle-1m";
    private string? _playTrialOfferId;

    public static CatalogPriceBuilder Monthly(int priceInr = 299) => new CatalogPriceBuilder().WithCycle(1).WithPrice(priceInr);

    public CatalogPriceBuilder WithId(Guid id) { _id = id; return this; }

    public CatalogPriceBuilder WithCycle(short months)
    {
        _cycleMonths = months;
        _playBasePlanId = $"cycle-{months}m";
        return this;
    }

    public CatalogPriceBuilder WithPrice(int priceInr) { _priceInr = priceInr; return this; }
    public CatalogPriceBuilder WithRazorpayPlanId(string? id) { _razorpayPlanId = id; return this; }
    public CatalogPriceBuilder Disabled() { _isEnabled = false; return this; }
    public CatalogPriceBuilder WithBasePlan(string? basePlanId) { _playBasePlanId = basePlanId; return this; }
    public CatalogPriceBuilder WithTrialOffer(string? offerId) { _playTrialOfferId = offerId; return this; }

    public CatalogPrice Build() => new(_id, _cycleMonths, _priceInr, _razorpayPlanId, _isEnabled, _playBasePlanId, _playTrialOfferId);
}

// Defaults: Room "Plus" (ListingLimit 3, Priority 2, Featured, JobsPosts 2), prices 1m 299 / 3m 799 / 12m 2999.
public sealed class CatalogPlanBuilder
{
    private Guid _id = TestIds.RoomPlus;
    private string _featureKey = ListingKinds.Room;
    private string _code = "PLUS";
    private string _name = "Plus";
    private string? _tagline;
    private bool _isDefault;
    private bool _isEnabled = true;
    private int _trialDays = 7;
    private int _sortOrder = 1;
    private string? _playProductId = "room_plus";
    private readonly Dictionary<string, int> _features = new()
    {
        [PlanFeatureKeys.ListingLimit] = 3,
        [PlanFeatureKeys.Priority] = 2,
        [PlanFeatureKeys.Featured] = 1,
        [PlanFeatureKeys.JobsPosts] = 2,
    };
    private List<CatalogPrice> _prices =
    [
        new CatalogPriceBuilder().WithId(TestIds.Price1m).WithCycle(1).WithPrice(299).WithTrialOffer("trial-7d").Build(),
        new CatalogPriceBuilder().WithId(TestIds.Price3m).WithCycle(3).WithPrice(799).Build(),
        new CatalogPriceBuilder().WithId(TestIds.Price12m).WithCycle(12).WithPrice(2999).Build(),
    ];

    public static CatalogPlanBuilder Plus() => new();

    // Free: ListingLimit 1, nothing else, no prices, no Play product.
    public static CatalogPlanBuilder Free() => new CatalogPlanBuilder().AsDefault();

    public CatalogPlanBuilder AsDefault()
    {
        _id = _featureKey == ListingKinds.Plot ? TestIds.PlotFree : TestIds.RoomFree;
        _code = "FREE";
        _name = "Free";
        _isDefault = true;
        _trialDays = 0;
        _sortOrder = 0;
        _playProductId = null;
        _features.Clear();
        _features[PlanFeatureKeys.ListingLimit] = 1;
        _prices = [];
        return this;
    }

    public CatalogPlanBuilder WithId(Guid id) { _id = id; return this; }
    public CatalogPlanBuilder ForFeature(string featureKey) { _featureKey = featureKey; return this; }
    public CatalogPlanBuilder WithCode(string code) { _code = code; return this; }
    public CatalogPlanBuilder WithName(string name) { _name = name; return this; }
    public CatalogPlanBuilder WithTagline(string? tagline) { _tagline = tagline; return this; }
    public CatalogPlanBuilder Disabled() { _isEnabled = false; return this; }
    public CatalogPlanBuilder WithTrialDays(int days) { _trialDays = days; return this; }
    public CatalogPlanBuilder WithSortOrder(int sortOrder) { _sortOrder = sortOrder; return this; }
    public CatalogPlanBuilder WithPlayProduct(string? productId) { _playProductId = productId; return this; }
    public CatalogPlanBuilder WithFeature(string key, int value) { _features[key] = value; return this; }
    public CatalogPlanBuilder WithoutFeature(string key) { _features.Remove(key); return this; }
    public CatalogPlanBuilder WithListingLimit(int value) => WithFeature(PlanFeatureKeys.ListingLimit, value);
    public CatalogPlanBuilder WithPriority(int value) => WithFeature(PlanFeatureKeys.Priority, value);
    public CatalogPlanBuilder WithFeatured(bool featured) => WithFeature(PlanFeatureKeys.Featured, featured ? 1 : 0);
    public CatalogPlanBuilder WithJobsPosts(int value) => WithFeature(PlanFeatureKeys.JobsPosts, value);
    public CatalogPlanBuilder WithPrices(params CatalogPrice[] prices) { _prices = [.. prices]; return this; }

    public CatalogPlan Build()
    {
        var features = new Dictionary<string, int>(_features);
        return new CatalogPlan(_id, _featureKey, _code, _name, _tagline, _isDefault, _isEnabled, _trialDays, _sortOrder,
            features, PlanFeatureValues.From(features), _prices.ToList(), _playProductId);
    }
}

// Default: Room Free + Room Plus + Plot Free + Plot Plus.
public sealed class PlanCatalogSnapshotBuilder
{
    private readonly List<CatalogPlan> _plans = [];
    private bool _defaults = true;

    public static CatalogPlan RoomFree => CatalogPlanBuilder.Free().Build();
    public static CatalogPlan RoomPlus => CatalogPlanBuilder.Plus().Build();
    public static CatalogPlan PlotFree => new CatalogPlanBuilder().ForFeature(ListingKinds.Plot).AsDefault().Build();

    public static CatalogPlan PlotPlus => CatalogPlanBuilder.Plus().ForFeature(ListingKinds.Plot).WithId(TestIds.PlotPlus)
        .WithPlayProduct("plot_plus").Build();

    public PlanCatalogSnapshotBuilder Empty() { _defaults = false; return this; }

    public PlanCatalogSnapshotBuilder With(params CatalogPlan[] plans)
    {
        _defaults = false;
        _plans.AddRange(plans);
        return this;
    }

    public PlanCatalogSnapshot Build()
        => new(_defaults ? new List<CatalogPlan> { RoomFree, RoomPlus, PlotFree, PlotPlus } : _plans);
}

public static class PaymentFeatureStates
{
    public static PaymentFeatureState On(int freeDays = 30) => new(true, freeDays, null, PaymentProviders.Play);

    public static PaymentFeatureState Off(int? freeLimit = null, int freeDays = 30) => new(false, freeDays, freeLimit, PaymentProviders.Play);
}

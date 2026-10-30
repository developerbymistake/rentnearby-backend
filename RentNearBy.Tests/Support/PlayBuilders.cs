namespace RentNearBy.Tests.Support;

// Default: room_plus, cycle-1m, auto-renewing, expiry Dates.Far, order GPA.1, INR 299.
public sealed class PlayLineItemBuilder
{
    private string _productId = "room_plus";
    private DateTime? _expiryTime = Dates.Far;
    private string? _basePlanId = "cycle-1m";
    private string? _offerId;
    private bool? _autoRenewEnabled = true;
    private bool _isPrepaid;
    private string? _latestOrderId = "GPA.1";
    private string? _deferredProductId;
    private long? _recurringPriceMicros = 299_000_000;
    private string? _recurringPriceCurrency = "INR";
    private string? _priceChangeMode;
    private string? _priceChangeState;

    public PlayLineItemBuilder WithProduct(string productId) { _productId = productId; return this; }
    public PlayLineItemBuilder WithExpiry(DateTime? expiry) { _expiryTime = expiry; return this; }
    public PlayLineItemBuilder WithBasePlan(string? basePlanId) { _basePlanId = basePlanId; return this; }
    public PlayLineItemBuilder WithOffer(string? offerId) { _offerId = offerId; return this; }
    public PlayLineItemBuilder WithAutoRenew(bool? enabled) { _autoRenewEnabled = enabled; return this; }
    public PlayLineItemBuilder Prepaid() { _isPrepaid = true; _autoRenewEnabled = null; return this; }
    public PlayLineItemBuilder WithOrder(string? orderId) { _latestOrderId = orderId; return this; }
    public PlayLineItemBuilder DeferredTo(string? productId) { _deferredProductId = productId; return this; }
    public PlayLineItemBuilder WithPrice(long? micros, string? currency) { _recurringPriceMicros = micros; _recurringPriceCurrency = currency; return this; }
    public PlayLineItemBuilder WithPriceChange(string? mode, string? state) { _priceChangeMode = mode; _priceChangeState = state; return this; }

    public PlaySubscriptionLineItem Build() => new(_productId, _expiryTime, _basePlanId, _offerId, _autoRenewEnabled, _isPrepaid,
        _latestOrderId, _deferredProductId, _recurringPriceMicros, _recurringPriceCurrency, _priceChangeMode, _priceChangeState);
}

// Default: Active, acknowledged, one line item (PlayLineItemBuilder defaults).
public sealed class PlaySnapshotBuilder
{
    private string _purchaseToken = "token-0000000000000000000001";
    private string _state = PlayStates.Active;
    private string _ackState = PlayAckStates.Acknowledged;
    private DateTime? _startTime = Dates.Past;
    private string? _linkedPurchaseToken;
    private string? _expiredPurchaseToken;
    private string? _regionCode = "IN";
    private bool _isTestPurchase;
    private string? _obfuscatedAccountId;
    private string? _obfuscatedProfileId;
    private string? _cancelReason;
    private DateTime? _pausedAutoResumeTime;
    private List<PlaySubscriptionLineItem> _lineItems = [new PlayLineItemBuilder().Build()];
    private string _rawJson = "{}";

    public PlaySnapshotBuilder WithToken(string token) { _purchaseToken = token; return this; }
    public PlaySnapshotBuilder WithState(string state) { _state = state; return this; }
    public PlaySnapshotBuilder WithAck(string ackState) { _ackState = ackState; return this; }
    public PlaySnapshotBuilder WithStart(DateTime? start) { _startTime = start; return this; }
    public PlaySnapshotBuilder LinkedTo(string? token) { _linkedPurchaseToken = token; return this; }
    public PlaySnapshotBuilder WithExpiredToken(string? token) { _expiredPurchaseToken = token; return this; }
    public PlaySnapshotBuilder WithRegion(string? region) { _regionCode = region; return this; }
    public PlaySnapshotBuilder AsTestPurchase() { _isTestPurchase = true; return this; }
    public PlaySnapshotBuilder WithAccount(string? accountId, string? profileId = null) { _obfuscatedAccountId = accountId; _obfuscatedProfileId = profileId; return this; }
    public PlaySnapshotBuilder WithCancelReason(string? reason) { _cancelReason = reason; return this; }
    public PlaySnapshotBuilder PausedUntil(DateTime? resume) { _pausedAutoResumeTime = resume; return this; }
    public PlaySnapshotBuilder WithLineItems(params PlaySubscriptionLineItem[] items) { _lineItems = [.. items]; return this; }
    public PlaySnapshotBuilder WithRawJson(string json) { _rawJson = json; return this; }

    public PlaySubscriptionSnapshot Build() => new(_purchaseToken, _state, _ackState, _startTime, _linkedPurchaseToken,
        _expiredPurchaseToken, _regionCode, _isTestPurchase, _obfuscatedAccountId, _obfuscatedProfileId, _cancelReason,
        _pausedAutoResumeTime, _lineItems.ToList(), _rawJson);
}

// Default: User1 buys Room Plus monthly (room_plus).
public sealed class PlayResolvedTargetBuilder
{
    private Guid _userId = TestIds.User1;
    private CatalogPlan _plan = CatalogPlanBuilder.Plus().Build();
    private CatalogPrice? _price;
    private string _accountKey = new('a', PlayRules.ObfuscatedIdLength);
    private CatalogPlan? _scheduledPlan;
    private string? _purchasedProductId;
    private bool _reattach;

    public PlayResolvedTargetBuilder ForUser(Guid userId) { _userId = userId; return this; }
    public PlayResolvedTargetBuilder WithPlan(CatalogPlan plan) { _plan = plan; return this; }
    public PlayResolvedTargetBuilder WithPrice(CatalogPrice price) { _price = price; return this; }
    public PlayResolvedTargetBuilder WithAccountKey(string key) { _accountKey = key; return this; }
    public PlayResolvedTargetBuilder Scheduled(CatalogPlan? plan) { _scheduledPlan = plan; return this; }
    public PlayResolvedTargetBuilder PurchasedAs(string productId) { _purchasedProductId = productId; return this; }
    public PlayResolvedTargetBuilder Reattach() { _reattach = true; return this; }

    public PlayResolvedTarget Build()
    {
        var price = _price ?? _plan.Prices.FirstOrDefault() ?? CatalogPriceBuilder.Monthly().Build();
        return new PlayResolvedTarget(_userId, _plan.FeatureKey, _plan, price, _accountKey, _scheduledPlan,
            _purchasedProductId ?? _plan.PlayProductId ?? string.Empty, _reattach);
    }
}

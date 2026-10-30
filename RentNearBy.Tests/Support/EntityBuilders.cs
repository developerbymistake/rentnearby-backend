namespace RentNearBy.Tests.Support;

// In-memory entities only (never attached to a DbContext by unit tests).
public sealed class UserPlanBuilder
{
    private readonly UserPlan _row = new()
    {
        Id = TestIds.UserPlan1,
        UserId = TestIds.User1,
        FeatureKey = ListingKinds.Room,
        PlanId = TestIds.RoomPlus,
        Source = UserPlanSources.Subscription,
        Status = UserPlanStatuses.Active,
        StartsAt = Dates.Past,
        EndsAt = Dates.Far,
        TotalPaidInr = 299,
        TotalDays = 30,
        CreatedAt = Dates.Past,
        UpdatedAt = Dates.Past,
    };

    public UserPlanBuilder WithId(Guid id) { _row.Id = id; return this; }
    public UserPlanBuilder ForUser(Guid userId) { _row.UserId = userId; return this; }
    public UserPlanBuilder ForFeature(string featureKey) { _row.FeatureKey = featureKey; return this; }
    public UserPlanBuilder OnPlan(Guid planId) { _row.PlanId = planId; return this; }
    public UserPlanBuilder WithSource(string source) { _row.Source = source; return this; }
    public UserPlanBuilder WithStatus(string status) { _row.Status = status; return this; }
    public UserPlanBuilder Between(DateTime startsAt, DateTime endsAt) { _row.StartsAt = startsAt; _row.EndsAt = endsAt; return this; }
    public UserPlanBuilder Paid(int totalPaidInr, int totalDays) { _row.TotalPaidInr = totalPaidInr; _row.TotalDays = totalDays; return this; }
    public UserPlanBuilder PlayOwned(Guid playSubscriptionId, string state = PlayStates.Active)
    {
        _row.PlaySubscriptionId = playSubscriptionId;
        _row.PlayState = state;
        _row.PlayAutoRenewEnabled = true;
        return this;
    }
    public UserPlanBuilder WithPlayTrialEndsAt(DateTime? at) { _row.PlayTrialEndsAt = at; return this; }
    public UserPlanBuilder WithCarryOverDays(int days) { _row.CarryOverDays = days; return this; }
    public UserPlanBuilder WithNote(string? note) { _row.Note = note; return this; }

    public UserPlan Build() => _row;
}

public sealed class PlanPurchaseBuilder
{
    private readonly PlanPurchase _row = new()
    {
        Id = TestIds.Purchase1,
        UserId = TestIds.User1,
        FeatureKey = ListingKinds.Room,
        PlanId = TestIds.RoomPlus,
        PlanNameSnapshot = "Plus",
        CycleMonths = 1,
        GrantedDays = 30,
        Intent = PurchaseIntents.New,
        Provider = PaymentProviders.Play,
        ListPriceInr = 299,
        PayableInr = 299,
        Status = PurchaseStatuses.Granted,
        CreatedAt = Dates.Past,
        ExpiresAt = Dates.Past.AddMinutes(30),
        GrantedAt = Dates.Past,
        PlayOrderId = "GPA.1",
        PlayProductId = "room_plus",
        PlayBasePlanId = "cycle-1m",
    };

    public PlanPurchaseBuilder WithId(Guid id) { _row.Id = id; return this; }
    public PlanPurchaseBuilder ForUser(Guid? userId) { _row.UserId = userId; return this; }
    public PlanPurchaseBuilder ForFeature(string featureKey) { _row.FeatureKey = featureKey; return this; }
    public PlanPurchaseBuilder OnPlan(Guid planId, string planName) { _row.PlanId = planId; _row.PlanNameSnapshot = planName; return this; }
    public PlanPurchaseBuilder WithCycle(short? months, int grantedDays) { _row.CycleMonths = months; _row.GrantedDays = grantedDays; return this; }
    public PlanPurchaseBuilder WithIntent(string intent) { _row.Intent = intent; return this; }
    public PlanPurchaseBuilder WithProvider(string provider) { _row.Provider = provider; return this; }
    public PlanPurchaseBuilder WithStatus(string status) { _row.Status = status; return this; }
    public PlanPurchaseBuilder WithAmounts(int list, int discount, int credit, int payable)
    {
        _row.ListPriceInr = list;
        _row.DiscountInr = discount;
        _row.CreditInr = credit;
        _row.PayableInr = payable;
        return this;
    }
    public PlanPurchaseBuilder WithCoupon(Guid? couponId, string? code) { _row.CouponId = couponId; _row.CouponCodeSnapshot = code; return this; }
    public PlanPurchaseBuilder WithPlay(string? orderId, string? productId, string? basePlanId, string? offerId, Guid? subscriptionId)
    {
        _row.PlayOrderId = orderId;
        _row.PlayProductId = productId;
        _row.PlayBasePlanId = basePlanId;
        _row.PlayOfferId = offerId;
        _row.PlaySubscriptionId = subscriptionId;
        return this;
    }
    public PlanPurchaseBuilder WithRazorpay(string? orderId, string? paymentId, string? refundId)
    {
        _row.RazorpayOrderId = orderId;
        _row.RazorpayPaymentId = paymentId;
        _row.RazorpayRefundId = refundId;
        return this;
    }
    public PlanPurchaseBuilder WithFailure(string? grantBlockedReason, string? failureReason)
    {
        _row.GrantBlockedReason = grantBlockedReason;
        _row.FailureReason = failureReason;
        return this;
    }
    public PlanPurchaseBuilder WithTimes(DateTime createdAt, DateTime? grantedAt, DateTime? refundedAt)
    {
        _row.CreatedAt = createdAt;
        _row.GrantedAt = grantedAt;
        _row.RefundedAt = refundedAt;
        return this;
    }
    public PlanPurchaseBuilder WithChargebackReviewAt(DateTime? at) { _row.PlayChargebackReviewAt = at; return this; }

    public PlanPurchase Build() => _row;
}

public sealed class PlaySubscriptionBuilder
{
    private readonly PlaySubscription _row = new()
    {
        Id = TestIds.Sub1,
        UserId = TestIds.User1,
        FeatureKey = ListingKinds.Room,
        PlanId = TestIds.RoomPlus,
        PlanPriceId = TestIds.Price1m,
        PurchaseToken = "token-0000000000000000000001",
        ProductId = "room_plus",
        PurchasedProductId = "room_plus",
        BasePlanId = "cycle-1m",
        MappingStatus = PlayMappingStatuses.Mapped,
        State = PlayStates.Active,
        AutoRenewEnabled = true,
        StartTime = Dates.Past,
        ExpiryTime = Dates.Far,
        LatestOrderId = "GPA.1",
        AcknowledgementState = PlayAckStates.Acknowledged,
        CreatedAt = Dates.Past,
        UpdatedAt = Dates.Past,
    };

    public PlaySubscriptionBuilder WithId(Guid id) { _row.Id = id; return this; }
    public PlaySubscriptionBuilder ForUser(Guid? userId) { _row.UserId = userId; return this; }
    public PlaySubscriptionBuilder WithToken(string token) { _row.PurchaseToken = token; return this; }
    public PlaySubscriptionBuilder WithProduct(string productId) { _row.ProductId = productId; _row.PurchasedProductId = productId; return this; }
    public PlaySubscriptionBuilder WithState(string state, DateTime? expiry) { _row.State = state; _row.ExpiryTime = expiry; return this; }
    public PlaySubscriptionBuilder WithMapping(string status, string? detachedReason = null)
    {
        _row.MappingStatus = status;
        _row.DetachedReason = detachedReason;
        return this;
    }
    public PlaySubscriptionBuilder WithAutoRenew(bool enabled) { _row.AutoRenewEnabled = enabled; return this; }
    public PlaySubscriptionBuilder WithAck(string state) { _row.AcknowledgementState = state; return this; }
    public PlaySubscriptionBuilder WithPrice(long? micros, string? currency) { _row.RecurringPriceMicros = micros; _row.RecurringPriceCurrency = currency; return this; }
    public PlaySubscriptionBuilder WithPendingAction(string? action, string? reason)
    {
        _row.PendingServerAction = action;
        _row.PendingActionReason = reason;
        return this;
    }

    public PlaySubscription Build() => _row;
}

public sealed class CouponBuilder
{
    private readonly Coupon _row = new()
    {
        Id = Guid.Parse("90000000-0000-0000-0000-000000000001"),
        Code = "SAVE10",
        Kind = CouponKinds.Percent,
        Value = 10,
        PerUserLimit = 1,
        Status = CouponStatuses.Active,
        CreatedAt = Dates.Past,
        UpdatedAt = Dates.Past,
    };

    public static CouponBuilder Percent(int value) => new CouponBuilder().WithKind(CouponKinds.Percent, value);
    public static CouponBuilder Flat(int value) => new CouponBuilder().WithKind(CouponKinds.Flat, value);
    public static CouponBuilder FreePlan() => new CouponBuilder().WithKind(CouponKinds.FreePlan, 0);

    public CouponBuilder WithCode(string code) { _row.Code = code; return this; }
    public CouponBuilder WithKind(string kind, int value) { _row.Kind = kind; _row.Value = value; return this; }
    public CouponBuilder WithMaxDiscount(int? maxDiscountInr) { _row.MaxDiscountInr = maxDiscountInr; return this; }
    public CouponBuilder WithMinPlanPrice(int? minPlanPriceInr) { _row.MinPlanPriceInr = minPlanPriceInr; return this; }
    public CouponBuilder ForFeature(string? featureKey) { _row.FeatureKey = featureKey; return this; }
    public CouponBuilder ForPlans(params Guid[] planIds) { _row.AllowedPlanIds = planIds; return this; }
    public CouponBuilder WithStatus(string status) { _row.Status = status; return this; }
    public CouponBuilder ValidBetween(DateTime? from, DateTime? until) { _row.ValidFrom = from; _row.ValidUntil = until; return this; }
    public CouponBuilder AutoApply(bool autoApply = true) { _row.AutoApply = autoApply; return this; }

    public Coupon Build() => _row;
}

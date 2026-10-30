using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Plans;

public class PlanPurchaseServiceTests
{
    [Theory]
    [InlineData("9876543210", "******3210")]
    [InlineData("+919876543210", "*********3210")]
    [InlineData("12345", "*2345")]
    [InlineData("1234", "1234")]
    [InlineData("123", "123")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void MaskPhone_KeepsLastFourOnly(string? phone, string? expected)
        => Assert.Equal(expected, PlanPurchaseService.MaskPhone(phone));

    [Fact]
    public void ToAdminDto_EveryMappedField_Copied()
    {
        var subscriptionId = Guid.Parse("77777777-0000-0000-0000-000000000001");
        var couponId = Guid.Parse("77777777-0000-0000-0000-000000000002");
        var createdAt = Dates.Utc(2026, 10, 1, 10, 0);
        var grantedAt = Dates.Utc(2026, 10, 1, 10, 5);
        var refundedAt = Dates.Utc(2026, 10, 2, 9, 0);
        var reviewAt = Dates.Utc(2026, 10, 3, 8, 0);
        var purchase = new PlanPurchaseBuilder()
            .WithId(TestIds.Purchase1)
            .ForUser(TestIds.User2)
            .ForFeature(ListingKinds.Plot)
            .OnPlan(TestIds.PlotPlus, "Plot Plus")
            .WithCycle(3, 90)
            .WithIntent(PurchaseIntents.Switch)
            .WithProvider(PaymentProviders.Play)
            .WithAmounts(list: 799, discount: 80, credit: 50, payable: 669)
            .WithCoupon(couponId, "SAVE10")
            .WithStatus(PurchaseStatuses.Refunded)
            .WithRazorpay("order_1", "pay_1", "rfnd_1")
            .WithFailure(GrantBlockedReasons.AmountMismatch, "mismatch")
            .WithTimes(createdAt, grantedAt, refundedAt)
            .WithPlay("GPA.7", "plot_plus", "cycle-3m", "intro", subscriptionId)
            .WithChargebackReviewAt(reviewAt)
            .Build();

        var dto = PlanPurchaseService.ToAdminDto(purchase, "9876543210");

        Assert.Equal(TestIds.Purchase1, dto.Id);
        Assert.Equal(TestIds.User2, dto.UserId);
        Assert.Equal("******3210", dto.UserPhoneMasked);
        Assert.Equal("Plot Plus", dto.PlanName);
        Assert.Equal(ListingKinds.Plot, dto.FeatureKey);
        Assert.Equal(PurchaseIntents.Switch, dto.Intent);
        Assert.Equal(3, dto.CycleMonths);
        Assert.Equal(90, dto.GrantedDays);
        Assert.Equal(799, dto.ListPriceInr);
        Assert.Equal(80, dto.DiscountInr);
        Assert.Equal(50, dto.CreditInr);
        Assert.Equal(669, dto.PayableInr);
        Assert.Equal("SAVE10", dto.CouponCode);
        Assert.Equal(PurchaseStatuses.Refunded, dto.Status);
        Assert.Equal(PaymentProviders.Play, dto.Provider);
        Assert.Equal("order_1", dto.RazorpayOrderId);
        Assert.Equal("pay_1", dto.RazorpayPaymentId);
        Assert.Equal("rfnd_1", dto.RazorpayRefundId);
        Assert.Equal(GrantBlockedReasons.AmountMismatch, dto.GrantBlockedReason);
        Assert.Equal("mismatch", dto.FailureReason);
        Assert.Equal(createdAt, dto.CreatedAt);
        Assert.Equal(grantedAt, dto.GrantedAt);
        Assert.Equal(refundedAt, dto.RefundedAt);
        Assert.Equal("GPA.7", dto.PlayOrderId);
        Assert.Equal("plot_plus", dto.PlayProductId);
        Assert.Equal("cycle-3m", dto.PlayBasePlanId);
        Assert.Equal("intro", dto.PlayOfferId);
        Assert.Equal(subscriptionId, dto.PlaySubscriptionId);
        Assert.Equal(reviewAt, dto.PlayChargebackReviewAt);
    }

    [Fact]
    public void ToAdminDto_DeletedUser_NoPhone()
    {
        var dto = PlanPurchaseService.ToAdminDto(new PlanPurchaseBuilder().ForUser(null).Build(), null);

        Assert.Null(dto.UserId);
        Assert.Null(dto.UserPhoneMasked);
    }

    public static TheoryData<string, int, int?, int, int> Discounts => new()
    {
        { CouponKinds.Percent, 10, null, 299, 29 },
        { CouponKinds.Percent, 50, 100, 299, 100 },
        { CouponKinds.Percent, 50, 500, 299, 149 },
        { CouponKinds.Percent, 100, null, 299, 299 },
        { CouponKinds.Flat, 500, null, 299, 299 },
        { CouponKinds.Flat, 50, null, 299, 50 },
        { CouponKinds.Flat, -5, null, 299, 0 },
        { CouponKinds.FreePlan, 0, null, 299, 299 },
        { "X", 50, null, 299, 0 },
    };

    [Theory]
    [MemberData(nameof(Discounts))]
    public void DiscountOf_KindAndValue_ReturnsDiscount(string kind, int value, int? maxDiscount, int list, int expected)
    {
        var coupon = new CouponBuilder().WithKind(kind, value).WithMaxDiscount(maxDiscount).Build();

        Assert.Equal(expected, PlanPurchaseService.DiscountOf(coupon, list));
    }

    [Theory]
    [InlineData(" abc-1 ", "ABC-1")]
    [InlineData("save10", "SAVE10")]
    [InlineData("SAVE10", "SAVE10")]
    public void NormalizeCode_TrimsAndUppercases(string code, string expected)
        => Assert.Equal(expected, PlanPurchaseService.NormalizeCode(code));

    private static readonly Guid OtherPlan = Guid.Parse("22222222-0000-0000-0000-000000000001");

    private static CatalogPrice Price(int cycle, int inr) => new CatalogPriceBuilder().WithCycle((short)cycle).WithPrice(inr).Build();

    [Fact]
    public void ReplacementModeOf_NoCurrentPrice_ChargeFullPrice()
    {
        var plan = CatalogPlanBuilder.Plus().Build();

        Assert.Equal(PlayReplacementModes.ChargeFullPrice,
            PlanPurchaseService.ReplacementModeOf(plan.Id, null, plan, Price(1, 299)));
    }

    [Theory]
    [InlineData(1, 3, "chargeFullPrice")]
    [InlineData(1, 12, "chargeFullPrice")]
    [InlineData(3, 1, "withoutProration")]
    [InlineData(1, 1, "withoutProration")]
    public void ReplacementModeOf_SamePlan_LongerCycleChargesNow(int fromCycle, int toCycle, string expected)
    {
        var plan = CatalogPlanBuilder.Plus().Build();

        Assert.Equal(expected, PlanPurchaseService.ReplacementModeOf(plan.Id, Price(fromCycle, 299), plan, Price(toCycle, 299)));
    }

    [Theory]
    [InlineData(3, 799, 1, 299, "chargeFullPrice")]
    [InlineData(1, 299, 1, 199, "deferred")]
    [InlineData(3, 897, 1, 299, "chargeFullPrice")]
    [InlineData(1, 299, 12, 2999, "deferred")]
    [InlineData(1, 199, 1, 299, "chargeFullPrice")]
    public void ReplacementModeOf_OtherPlan_ComparesMonthlyPrice(int oldCycle, int oldInr, int newCycle, int newInr, string expected)
    {
        var plan = CatalogPlanBuilder.Plus().Build();

        Assert.Equal(expected, PlanPurchaseService.ReplacementModeOf(OtherPlan, Price(oldCycle, oldInr), plan, Price(newCycle, newInr)));
    }
}

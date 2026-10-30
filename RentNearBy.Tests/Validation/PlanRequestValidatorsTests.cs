using FluentValidation.TestHelper;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Validation;

public class PlanRequestValidatorsTests
{
    private static readonly QuoteRequestValidator Quote = new();
    private static readonly CreatePlanOrderRequestValidator CreateOrder = new();

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(12, true)]
    [InlineData(2, false)]
    [InlineData(0, false)]
    public void Quote_CycleMonths(int months, bool valid)
    {
        var result = Quote.TestValidate(new QuoteRequest(TestIds.RoomPlus, months, null));

        if (valid) result.ShouldNotHaveValidationErrorFor(x => x.CycleMonths);
        else result.ShouldHaveValidationErrorFor(x => x.CycleMonths).WithErrorMessage("CycleMonths must be 1, 3 or 12");
    }

    [Fact]
    public void Quote_EmptyPlanId_Error()
        => Quote.TestValidate(new QuoteRequest(Guid.Empty, 1, null)).ShouldHaveValidationErrorFor(x => x.PlanId);

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void Quote_CouponCodeLength(int length, bool valid)
    {
        var result = Quote.TestValidate(new QuoteRequest(TestIds.RoomPlus, 1, new string('C', length)));

        if (valid) result.ShouldNotHaveAnyValidationErrors();
        else result.ShouldHaveValidationErrorFor(x => x.CouponCode);
    }

    [Fact]
    public void Quote_NullCoupon_Valid()
        => Quote.TestValidate(new QuoteRequest(TestIds.RoomPlus, 12, null, UseTrial: true)).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData(2, 10, false)]
    [InlineData(12, 32, true)]
    [InlineData(1, 33, false)]
    public void CreateOrder_CycleAndCoupon(int months, int couponLength, bool valid)
    {
        var result = CreateOrder.TestValidate(new CreatePlanOrderRequest(TestIds.RoomPlus, months, new string('C', couponLength)));

        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public void CreateOrder_EmptyPlanId_Error()
        => CreateOrder.TestValidate(new CreatePlanOrderRequest(Guid.Empty, 1, null)).ShouldHaveValidationErrorFor(x => x.PlanId);

    private static VerifyPlanPurchaseRequest Verify(string order = "order_1", string payment = "pay_1", string signature = "sig")
        => new() { RazorpayOrderId = order, RazorpayPaymentId = payment, RazorpaySignature = signature };

    [Fact]
    public void VerifyPlanPurchase_Valid()
        => new VerifyPlanPurchaseRequestValidator().TestValidate(Verify()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void VerifyPlanPurchase_EmptyIds_Errors()
    {
        var result = new VerifyPlanPurchaseRequestValidator().TestValidate(Verify("", "", ""));

        result.ShouldHaveValidationErrorFor(x => x.RazorpayOrderId);
        result.ShouldHaveValidationErrorFor(x => x.RazorpayPaymentId);
        result.ShouldHaveValidationErrorFor(x => x.RazorpaySignature);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void VerifyPlanPurchase_SignatureLength(int length, bool valid)
        => Assert.Equal(valid, new VerifyPlanPurchaseRequestValidator().TestValidate(Verify(signature: new string('s', length))).IsValid);

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void VerifyPlanPurchase_OrderIdLength(int length, bool valid)
        => Assert.Equal(valid, new VerifyPlanPurchaseRequestValidator().TestValidate(Verify(order: new string('o', length))).IsValid);

    [Fact]
    public void StartTrial_EmptyPlanId_Error()
        => new StartTrialRequestValidator().TestValidate(new StartTrialRequest(Guid.Empty)).ShouldHaveValidationErrorFor(x => x.PlanId);

    [Fact]
    public void StartTrial_PlanId_Valid()
        => new StartTrialRequestValidator().TestValidate(new StartTrialRequest(TestIds.RoomPlus)).ShouldNotHaveAnyValidationErrors();

    private static AdminGrantPlanRequest Grant(int days = 30, string reason = "Goodwill", string key = "key-1")
        => new(TestIds.RoomPlus, days, reason, key);

    [Theory]
    [InlineData(1, true)]
    [InlineData(730, true)]
    [InlineData(0, false)]
    [InlineData(731, false)]
    public void AdminGrant_Days(int days, bool valid)
    {
        var result = new AdminGrantPlanRequestValidator().TestValidate(Grant(days: days));

        if (valid) result.ShouldNotHaveAnyValidationErrors();
        else result.ShouldHaveValidationErrorFor(x => x.Days);
    }

    [Theory]
    [InlineData(300, true)]
    [InlineData(301, false)]
    [InlineData(0, false)]
    public void AdminGrant_ReasonLength(int length, bool valid)
        => Assert.Equal(valid, new AdminGrantPlanRequestValidator().TestValidate(Grant(reason: new string('r', length))).IsValid);

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    [InlineData(0, false)]
    public void AdminGrant_IdempotencyKeyLength(int length, bool valid)
        => Assert.Equal(valid, new AdminGrantPlanRequestValidator().TestValidate(Grant(key: new string('k', length))).IsValid);

    [Fact]
    public void AdminGrant_EmptyPlanId_Error()
        => new AdminGrantPlanRequestValidator().TestValidate(new AdminGrantPlanRequest(Guid.Empty, 30, "r", "k"))
            .ShouldHaveValidationErrorFor(x => x.PlanId);

    [Theory]
    [InlineData(true, 300, true)]
    [InlineData(false, 10, false)]
    [InlineData(true, 301, false)]
    [InlineData(true, 0, false)]
    public void AdminRevoke_IdAndReason(bool hasId, int reasonLength, bool valid)
    {
        var request = new AdminRevokePlanRequest(hasId ? TestIds.UserPlan1 : Guid.Empty, new string('r', reasonLength));

        Assert.Equal(valid, new AdminRevokePlanRequestValidator().TestValidate(request).IsValid);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(501, false)]
    [InlineData(0, false)]
    public void AdminRefund_ReasonLength(int length, bool valid)
        => Assert.Equal(valid, new AdminRefundPurchaseRequestValidator().TestValidate(new AdminRefundPurchaseRequest(new string('r', length))).IsValid);

    [Fact]
    public void SwapLive_EmptyId_Error()
        => new SwapLiveRequestValidator().TestValidate(new SwapLiveRequest(Guid.Empty)).ShouldHaveValidationErrorFor(x => x.PauseListingId);

    [Fact]
    public void SwapLive_Id_Valid()
        => new SwapLiveRequestValidator().TestValidate(new SwapLiveRequest(Guid.NewGuid())).ShouldNotHaveAnyValidationErrors();
}

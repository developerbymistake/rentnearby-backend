namespace RentNearBy.Core.DTOs.Requests;

public record QuoteRequest(Guid PlanId, int CycleMonths, string? CouponCode, bool UseTrial = false);

public record CreatePlanOrderRequest(Guid PlanId, int CycleMonths, string? CouponCode, bool UseTrial = false);

// Same shape as the Razorpay checkout callback (VerifyPaymentRequest).
public class VerifyPlanPurchaseRequest : VerifyPaymentRequest
{
}

public record StartTrialRequest(Guid PlanId);

public record SwapLiveRequest(Guid PauseListingId);

public record PlanFeatureInput(string Key, int Value);

public record PlanPriceInput(int CycleMonths, int PriceInr, string? RazorpayPlanId, bool IsEnabled = true,
    string? PlayBasePlanId = null, string? PlayTrialOfferId = null);

// POST/PUT /admin/plans. Features and prices are replaced as a set.
public record UpsertPlanRequest(
    string FeatureKey,
    string Code,
    string Name,
    string? Tagline,
    bool IsEnabled,
    int TrialDays,
    int SortOrder,
    List<PlanFeatureInput>? Features,
    List<PlanPriceInput>? Prices,
    string? PlayProductId = null);

// IdempotencyKey is client-generated per submit; a replay returns the same purchase.
public record AdminGrantPlanRequest(Guid PlanId, int Days, string Reason, string IdempotencyKey);

public record AdminRevokePlanRequest(Guid UserPlanId, string Reason);

public record AdminRefundPurchaseRequest(string Reason);

namespace RentNearBy.Core.DTOs.Requests;

public record QuoteRequest(Guid PlanId, int CycleMonths, string? CouponCode);

public record CreatePlanOrderRequest(Guid PlanId, int CycleMonths, string? CouponCode);

// Same shape as the Razorpay checkout callback (VerifyPaymentRequest).
public class VerifyPlanPurchaseRequest : VerifyPaymentRequest
{
}

public record StartTrialRequest(Guid PlanId);

public record SwapLiveRequest(Guid PauseListingId);

public record PlanFeatureInput(string Key, int Value);

public record PlanPriceInput(int CycleMonths, int PriceInr, string? RazorpayPlanId, bool IsEnabled = true);

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
    List<PlanPriceInput>? Prices);

// IdempotencyKey is client-generated per submit; a replay returns the same purchase.
public record AdminGrantPlanRequest(Guid PlanId, int Days, string Reason, string IdempotencyKey);

public record AdminRevokePlanRequest(Guid UserPlanId, string Reason);

public record AdminRefundPurchaseRequest(string Reason);

namespace RentNearBy.Core.DTOs.Responses;

// Server-computed price breakdown; the client never computes money.
public class QuoteDto
{
    public Guid PlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public int CycleMonths { get; set; }
    public string Intent { get; set; } = string.Empty;
    public int ListPriceInr { get; set; }
    public int DiscountInr { get; set; }
    public int CreditInr { get; set; }
    public int PayableInr { get; set; }
    public int GrantedDays { get; set; }
    public bool RequiresPayment { get; set; }
    public QuoteCouponDto? Coupon { get; set; }
    public bool AutoCoupon { get; set; }
    public string Provider { get; set; } = string.Empty;
    public bool IsTrial { get; set; }
    public string? PlayReplacementMode { get; set; } // PlayReplacementModes.*
    // The app must show the store price, not ListPriceInr.
    public bool StorePriceRequired { get; set; }
}

public class QuoteCouponDto
{
    public string Code { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Value { get; set; }
}

namespace RentNearBy.Core.Entities;

public class Coupon
{
    public Guid Id { get; set; }
    public string? Code { get; set; } // null for non-typed triggers (e.g. the welcome bonus)
    public int CoinValue { get; set; }
    public string TriggerType { get; set; } = string.Empty; // RentNearBy.Core.Models.WellKnownCoupons.*Trigger
    public int PerUserLimit { get; set; } = 1; // hard-enforced at the DB layer (check constraint) — always 1 in v1
    public int? MaxTotalRedemptions { get; set; } // null = unlimited
    public int CurrentRedemptions { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
    public string Status { get; set; } = string.Empty; // RentNearBy.Core.Models.CouponStatuses.*
    public Guid? CreatedBy { get; set; } // admin user id, null for system-seeded coupons
    public string? CampaignLabel { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

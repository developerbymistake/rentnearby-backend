namespace RentNearBy.Core.Entities;

// Reserved at order creation, Consumed at grant, Released when the order fails or expires.
public class CouponRedemption
{
    public Guid Id { get; set; }
    public Guid CouponId { get; set; }
    public Guid? UserId { get; set; }
    public Guid PurchaseId { get; set; }
    public int DiscountInr { get; set; }
    public string Status { get; set; } = string.Empty; // CouponRedemptionStatuses.*
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Coupon Coupon { get; set; } = null!;
}

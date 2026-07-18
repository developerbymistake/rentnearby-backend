namespace RentNearBy.Core.Entities;

public class CouponRedemption
{
    public Guid Id { get; set; }
    public Guid CouponId { get; set; }
    public Guid UserId { get; set; }
    public DateTime RedeemedAt { get; set; }

    public Coupon Coupon { get; set; } = null!;
    public User User { get; set; } = null!;
}

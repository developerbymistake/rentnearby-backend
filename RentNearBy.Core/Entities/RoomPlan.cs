namespace RentNearBy.Core.Entities;

public class RoomPlan
{
    public Guid Id { get; set; }
    public string PlanType { get; set; } // "FREE" or "PAID"
    public int Days { get; set; }
    public int RoomLimit { get; set; }
    // Both fields are coin amounts, not rupees, since the coin economy replaced direct per-listing
    // Razorpay payment — GoLiveHandlers.GoLiveRoom spends OriginalPrice coins via ICoinWalletService.
    public int Price { get; set; }          // Sticker coin amount (MRP, shown with strikethrough)
    public int DiscountPercent { get; set; } = 0;
    public int OriginalPrice { get; set; } = 0; // Actual coin cost to Go Live on this plan
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

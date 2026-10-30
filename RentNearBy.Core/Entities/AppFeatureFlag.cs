using RentNearBy.Core.Models;

namespace RentNearBy.Core.Entities;

// Generic, reusable global feature-flag table.
// First (and currently only) row: the payment kill switch (RentNearBy.Core.Models.AppFeatureKeys.PaymentEnabled).
public class AppFeatureFlag
{
    public Guid Id { get; set; }
    public string FeatureKey { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int? FreeDurationDays { get; set; }
    // Rooms/Plots listing limit while payment is off; null = the default plan's limit.
    public int? FreeModeListingLimit { get; set; }
    // PaymentProviders.Play | PaymentProviders.Razorpay: which gateway new orders use.
    public string PaymentProvider { get; set; } = PaymentProviders.Play;
    public Guid? UpdatedByAdminId { get; set; }
    public Admin? UpdatedByAdmin { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Reason { get; set; }
}

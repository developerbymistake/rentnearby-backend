namespace RentNearBy.Core.Entities;

// Generic, reusable global feature-flag table — deliberately not folded into CreditFeature or
// ListingLimitSetting, both of which model a different concept (what credits buy / creation caps).
// First (and currently only) row: the payment kill switch (RentNearBy.Core.Models.AppFeatureKeys.PaymentEnabled).
public class AppFeatureFlag
{
    public Guid Id { get; set; }
    public string FeatureKey { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int? FreeDurationDays { get; set; }
    public Guid? UpdatedByAdminId { get; set; }
    public Admin? UpdatedByAdmin { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Reason { get; set; }
}

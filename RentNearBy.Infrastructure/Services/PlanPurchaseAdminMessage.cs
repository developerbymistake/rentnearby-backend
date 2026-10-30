namespace RentNearBy.Infrastructure.Services;

public class PlanPurchaseAdminMessage
{
    public string Kind { get; set; } = string.Empty;
    public Guid? PurchaseId { get; set; }
    public string? PlayOrderId { get; set; }
}

public static class PlanPurchaseAdminKinds
{
    public const string Granted = "Granted";
    public const string Renewed = "Renewed";
    public const string Switched = "Switched";
    public const string Recovered = "Recovered";
    public const string ChargebackReview = "ChargebackReview";
}

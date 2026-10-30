namespace RentNearBy.Core.DTOs.Responses;

// GET /plans/me (also returned by POST /plans/trial). FeatureKey null = Seeker, always free.
public class MyPlanDto
{
    public bool PaymentEnabled { get; set; }
    public int FreeDays { get; set; }
    public string? FeatureKey { get; set; }
    public MyPlanRefDto? Plan { get; set; }
    public bool IsPaid { get; set; }
    public string? Source { get; set; }
    public string? Status { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public int? DaysLeft { get; set; }
    public MyPlanLimitsDto Limits { get; set; } = new();
    public bool TrialAvailable { get; set; }
    public Guid? OpenPurchaseId { get; set; }
    public int PausedByPlanCount { get; set; }
    public string PaymentProvider { get; set; } = string.Empty;
    public int CarryOverDays { get; set; }
    // Non-null exactly when Play is.
    public string? ManageUrl { get; set; }
    public MyPlanPlayDto? Play { get; set; }
}

public class MyPlanRefDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}

public class MyPlanLimitsDto
{
    public int ListingLimit { get; set; }
    public int Used { get; set; }
    public int Live { get; set; }
    public int Priority { get; set; }
    public bool Featured { get; set; }
    public int JobsPosts { get; set; }
}

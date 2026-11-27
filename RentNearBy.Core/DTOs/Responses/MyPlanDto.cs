namespace RentNearBy.Core.DTOs.Responses;

// GET /plans/me (also returned by POST /plans/trial). FeatureKey null = Seeker, always free.
public class MyPlanDto
{
    // FB-50: Home's plan card state (OwnerPlanCardStates), additive; null for a Seeker.
    public string? State { get; set; }
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
    // Owner roles M3 (contract 2.4, AU-18, AU-21): rows hidden by the plan (PausedByPlanCount = their sum), visible usage for the
    // Profile line, and the payment start facts.
    public Owner.PlanHiddenDto Hidden { get; set; } = Owner.PlanHiddenDto.None;
    public Owner.PlanUsageDto Usage { get; set; } = new(0, 0, 0);
    public DateOnly? PaymentStartsOn { get; set; }
    public bool PlansStartedToday { get; set; }
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
    public int Photos { get; set; }
    public int RoomTypes { get; set; }
    public int Routes { get; set; }
    public int RingWeight { get; set; }
    public int CalendarEntries { get; set; }
    public int BlockedRanges { get; set; }
    public int UpcomingDays { get; set; }
    public int MonthsAhead { get; set; }
    public bool MonthSummary { get; set; }
    public bool Reminders { get; set; }
    public int SpecialPrices { get; set; }
    public bool DemandCount { get; set; }
    // Stay quotations: the monthly cap, this IST month's counted downloads (0 when the counter is from an older month) and the reset time.
    // Named after the plan key (every key has a field here); the wire name is the app contract's "quotationsPerMonth".
    [System.Text.Json.Serialization.JsonPropertyName("quotationsPerMonth")]
    public int StayQuotationsPerMonth { get; set; }
    public int QuotationsUsed { get; set; }
    public DateTime? QuotationsResetsAt { get; set; }
}

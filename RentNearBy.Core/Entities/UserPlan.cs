namespace RentNearBy.Core.Entities;

// Paid/trial/granted entitlement. Free is never a row: no Active row = the feature's default plan.
public class UserPlan
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string FeatureKey { get; set; } = string.Empty;
    public Guid PlanId { get; set; }
    public string Source { get; set; } = string.Empty; // UserPlanSources.*
    public string Status { get; set; } = string.Empty; // UserPlanStatuses.*
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    // Money and days accumulated on this row; drive the Switch credit (paid * remaining / total).
    public int TotalPaidInr { get; set; }
    public int TotalDays { get; set; }
    public DateTime? ReminderSentAt { get; set; }
    public Guid? GrantedByAdminId { get; set; }
    public string? Note { get; set; }
    // Play-owned when set: EndsAt follows Google's expiryTime, never the expiry sweep's clock.
    public Guid? PlaySubscriptionId { get; set; }
    public bool? PlayAutoRenewEnabled { get; set; }
    public string? PlayState { get; set; }             // PlayStates.*
    public DateTime? PlayTrialEndsAt { get; set; }
    public int CarryOverDays { get; set; }
    public Guid? PlayScheduledPlanId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Plan Plan { get; set; } = null!;
}

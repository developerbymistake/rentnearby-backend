namespace RentNearBy.Core.DTOs.Responses;

public class UserPlanDto
{
    public Guid Id { get; set; }
    public string FeatureKey { get; set; } = string.Empty;
    public Guid PlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public int TotalPaidInr { get; set; }
    public int TotalDays { get; set; }
    public Guid? GrantedByAdminId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public int CarryOverDays { get; set; }
    public AdminPlayDetailsDto? Play { get; set; }
}

// GET /admin/users/{id}/plans
public class AdminUserPlansDto
{
    public UserPlanDto? Current { get; set; }
    public List<UserPlanDto> History { get; set; } = new();
    public List<AdminPurchaseDto> Purchases { get; set; } = new();
    public List<AdminPlaySubscriptionDto> PlaySubscriptions { get; set; } = new();
    // Owner roles A6 (AU-21): the user's limits for his role, rows hidden by the plan and visible counts; null for a Seeker.
    public MyPlanLimitsDto? Limits { get; set; }
    public Owner.OwnerHiddenDto? Hidden { get; set; }
    public Owner.AdminPlanUsageDto? Usage { get; set; }
}

// AdminUserDto.Plan: the active non-default plan, projected in the user list (no N+1).
public class AdminUserPlanSummaryDto
{
    public string FeatureKey { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public DateTime EndsAt { get; set; }
    public string Source { get; set; } = string.Empty;
}

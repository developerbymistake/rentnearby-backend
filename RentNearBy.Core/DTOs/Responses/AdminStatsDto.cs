namespace RentNearBy.Core.DTOs.Responses;

public class AdminStatsDto
{
    public int TotalUsers { get; set; }
    public int ActiveDistricts { get; set; }
    public int TotalEarnings { get; set; }
    public int CurrentMonthEarnings { get; set; }
    public long TotalEarningsPaise { get; set; }
    public long CurrentMonthEarningsPaise { get; set; }
    public int ActivePaidUsers { get; set; }
    public List<AdminPlanUsersDto> PlanUsers { get; set; } = new();
    public int TripEnquiriesToday { get; set; }
    public int TripEnquiriesOpen { get; set; }
    public int TripProblems7d { get; set; }
    public int TripConfirmedThisWeek { get; set; }
    public int TripLeadsAnsweredPct { get; set; }
}

public class AdminPlanUsersDto
{
    public string FeatureKey { get; set; } = string.Empty;
    public Guid PlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public int PaidUsers { get; set; }
    public int TrialUsers { get; set; }
    public int GrantedUsers { get; set; }
}

namespace RentNearBy.Core.DTOs.Responses;

public class AdminStatsDto
{
    public int TotalUsers { get; set; }
    public int TotalListings { get; set; }
    public int ActiveListings { get; set; }
    public int TotalPlotListings { get; set; }
    public int ActivePlotListings { get; set; }
    public int ActiveDistricts { get; set; }
    public Dictionary<string, int> RoomListingsByDistrict { get; set; } = new();
    public int TotalEarnings { get; set; }
    public int CurrentMonthEarnings { get; set; }
    public int ActivePaidUsers { get; set; }
    public List<AdminPlanUsersDto> PlanUsers { get; set; } = new();
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

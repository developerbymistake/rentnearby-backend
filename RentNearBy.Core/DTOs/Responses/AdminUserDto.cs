namespace RentNearBy.Core.DTOs.Responses;

public class AdminUserDto
{
    public Guid Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsActive { get; set; }
    public bool HasUsedFreePlan { get; set; }
    public DateTime CreatedAt { get; set; }
    public int TotalListings { get; set; }
    public int ActiveListings { get; set; }
    public AdminMembershipDto? CurrentMembership { get; set; }
    public AdminPlotMembershipDto? CurrentPlotMembership { get; set; }
}

public class AdminMembershipDto
{
    public Guid Id { get; set; }
    public string PlanType { get; set; } = string.Empty;
    public DateTime ValidFrom { get; set; }
    public DateTime ValidUntil { get; set; }
    public int MaxRooms { get; set; }
    public bool IsActive { get; set; }
}

public class AdminPlotMembershipDto
{
    public Guid Id { get; set; }
    public string PlanType { get; set; } = string.Empty;
    public DateTime ValidFrom { get; set; }
    public DateTime ValidUntil { get; set; }
    public int MaxPlots { get; set; }
    public bool IsActive { get; set; }
}

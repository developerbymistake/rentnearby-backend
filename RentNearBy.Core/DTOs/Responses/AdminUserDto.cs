namespace RentNearBy.Core.DTOs.Responses;

public class AdminUserDto
{
    public Guid Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public bool IsPhoneVerified { get; set; }
    public string? Name { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public int TotalListings { get; set; }
    public int ActiveListings { get; set; }
    public int TotalPlotListings { get; set; }
    public int ActivePlotListings { get; set; }
    public int LiveRoomListings { get; set; }
    public int LivePlotListings { get; set; }
    public int TotalStays { get; set; }
    public int LiveStayListings { get; set; }   // stays under review: the only stay state that blocks a role change
    public string? StayStatus { get; set; }
    public int TotalTaxis { get; set; }
    public int LiveTaxiListings { get; set; }   // taxis under review
    public string? TaxiStatus { get; set; }
    public int WalletBalance { get; set; }
    public string Role { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
}

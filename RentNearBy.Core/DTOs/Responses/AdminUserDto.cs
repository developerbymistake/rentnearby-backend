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
    public int WalletBalance { get; set; }
}

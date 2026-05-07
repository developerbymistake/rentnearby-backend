namespace RentNearBy.Core.DTOs.Responses;

public class AdminStatsDto
{
    public int TotalUsers { get; set; }
    public int TotalListings { get; set; }
    public int ActiveListings { get; set; }
    public Dictionary<string, int> ListingsByDistrict { get; set; } = new();
}

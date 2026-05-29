namespace RentNearBy.Core.DTOs.Responses;

public class PlotDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public decimal AreaValue { get; set; }
    public string AreaUnit { get; set; } = string.Empty;
    public decimal AreaSqft { get; set; }
    public string PlotType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? Address { get; set; }
    public Guid DistrictId { get; set; }
    public string? DistrictName { get; set; }
    public Guid? CityId { get; set; }
    public string? CityName { get; set; }
    public bool IsActive { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    public string? OwnerEmail { get; set; }
    public List<string> Photos { get; set; } = new();
    public DateTime? ValidUntil { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

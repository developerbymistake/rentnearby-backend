namespace RentNearBy.Core.DTOs.Responses;

public class HomePlotDto
{
    public Guid Id { get; set; }
    public decimal AreaValue { get; set; }
    public string AreaUnit { get; set; } = string.Empty;
    public string? PlotTypeName { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? CityName { get; set; }
    public string DistrictName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

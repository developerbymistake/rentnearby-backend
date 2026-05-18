namespace RentNearBy.Core.DTOs.Responses;

public class NearbyPlotDto
{
    public Guid Id { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public decimal AreaValue { get; set; }
    public string AreaUnit { get; set; } = string.Empty;
    public string PlotType { get; set; } = string.Empty;
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    public string? ThumbnailUrl { get; set; }
    public double DistanceKm { get; set; }
    public bool IsActive { get; set; }
}

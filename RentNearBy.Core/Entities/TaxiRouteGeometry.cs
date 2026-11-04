namespace RentNearBy.Core.Entities;

// Road line of one taxi route, written only by TaxiRouteGeometryService.
public class TaxiRouteGeometry
{
    public Guid RouteId { get; set; }
    public string EndsHash { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string? GeometryJson { get; set; }
    public decimal? DistanceKm { get; set; }
    public int? Minutes { get; set; }
    public short Attempts { get; set; }
    public DateTime UpdatedAt { get; set; }
}

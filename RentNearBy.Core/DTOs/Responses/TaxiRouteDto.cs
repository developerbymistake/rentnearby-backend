namespace RentNearBy.Core.DTOs.Responses;

public class TaxiRouteDto
{
    public Guid Id { get; set; }
    public short RouteIndex { get; set; }
    public string FromName { get; set; } = string.Empty;
    public string? FromLabel { get; set; }
    public decimal FromLatitude { get; set; }
    public decimal FromLongitude { get; set; }
    public string ToName { get; set; } = string.Empty;
    public string? ToLabel { get; set; }
    public decimal ToLatitude { get; set; }
    public decimal ToLongitude { get; set; }
    public int Fare { get; set; }
    public string TripType { get; set; } = string.Empty;
}

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
    public long? FromCityKey { get; set; }
    public string? FromCityName { get; set; }
    public long? ToCityKey { get; set; }
    public string? ToCityName { get; set; }
    public bool CityKeysChosen { get; set; }
    // Both ends are live cities, so taxi requests can match this route.
    public bool ActiveInRequests => FromCityKey != null && ToCityKey != null;
    public bool FromOtherPlace => CityKeysChosen && FromCityKey == null;
    public bool ToOtherPlace => CityKeysChosen && ToCityKey == null;
    public int Fare { get; set; }
    public string TripType { get; set; } = string.Empty;
}

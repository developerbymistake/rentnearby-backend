namespace RentNearBy.Core.Entities;

// Both ends come from the place search; FromPoint/ToPoint are stored computed geography columns.
public class TaxiRoute
{
    public Guid Id { get; set; }
    public Guid TaxiListingId { get; set; }
    public short RouteIndex { get; set; }

    public string FromName { get; set; } = string.Empty;
    public string? FromLabel { get; set; }
    public decimal FromLatitude { get; set; }
    public decimal FromLongitude { get; set; }

    public string ToName { get; set; } = string.Empty;
    public string? ToLabel { get; set; }
    public decimal ToLatitude { get; set; }
    public decimal ToLongitude { get; set; }

    // Atlas city keys of both ends (TaxiRouteCityKeys only). Chosen: the driver picked them from the city list and a
    // null key is an other place the backfill never fills; else they are found from the pins (old builds).
    public long? FromCityKey { get; set; }
    public long? ToCityKey { get; set; }
    public bool CityKeysChosen { get; set; }

    public int Fare { get; set; }
    public string TripType { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    // Hidden by the owner's plan, never deleted (contract 3.3); hidden rows hold RouteIndex 101 and up and never ring.
    public bool HiddenByPlan { get; set; }
}

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

    public int Fare { get; set; }
    public string TripType { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

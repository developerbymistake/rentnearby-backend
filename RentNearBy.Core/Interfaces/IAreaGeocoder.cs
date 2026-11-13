namespace RentNearBy.Core.Interfaces;

// The address parts of a reverse lookup that decide the parent city.
public sealed record AreaAddress(string? City, string? Town, string? Village, string? County);

public interface IAreaGeocoder
{
    // null = the lookup failed or was cancelled; an AreaAddress with every part null = answered, no place name.
    Task<AreaAddress?> ReverseAreaAsync(double lat, double lng, CancellationToken ct = default);
}

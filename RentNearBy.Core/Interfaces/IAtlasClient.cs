using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface IAtlasClient
{
    bool IsConfigured { get; }
    // null on 404; throws AtlasUnavailableException on anything else.
    Task<AtlasCitySpots?> GetCitySpotsAsync(long cityKey, CancellationToken ct = default);
}

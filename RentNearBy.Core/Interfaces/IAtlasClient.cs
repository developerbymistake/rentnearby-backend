using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface IAtlasClient
{
    bool IsConfigured { get; }
    // null on 404; throws AtlasUnavailableException on anything else.
    Task<AtlasCitySpots?> GetCitySpotsAsync(long cityKey, CancellationToken ct = default);
    // Throws AtlasUnavailableException on any non-200 (a 404 here is a broken Atlas, not "no cities").
    Task<AtlasCityPage> SearchCitiesAsync(string? q, int page, int pageSize, bool readyOnly = false,
        bool popularOnly = false, Guid? stateId = null, CancellationToken ct = default);
    // Throws AtlasUnavailableException on any non-200.
    Task<AtlasCatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default);
}

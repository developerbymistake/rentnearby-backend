using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ITaxiListingRepository : IRepository<TaxiListing>
{
    // Tracked, for the driver's own writes: route edits are applied on the loaded collection.
    Task<TaxiListing?> GetLiveByUserAsync(Guid userId);
    Task<string?> GetStatusByUserAsync(Guid userId);
    // Both include soft-deleted rows.
    Task<bool> HasEverApprovedAsync(Guid userId);
    Task<bool> HasSubmittedHistoryAsync(Guid userId);
    Task<int> TouchAllByUserAsync(Guid userId);
    // !IsDeleted only; the handler applies the public-or-owner rule.
    Task<TaxiListing?> GetByIdWithAllAsync(Guid id);
    // Public share link: only an Approved, available, not deleted taxi resolves.
    Task<TaxiListing?> GetBySlugWithAllAsync(string slug);
    // Survives IsDeleted so report review still shows what was reported.
    Task<TaxiListing?> GetByIdForAdminAsync(Guid id);
    Task<bool> IsPlateTakenAsync(string numberPlate, Guid exceptUserId);
    Task<(IReadOnlyList<TaxiBrowseDto> Items, bool HasMore)> BrowseLocalPagedAsync(
        Guid districtId, Guid? cityId, string? category, int page, int pageSize);
    Task<(IReadOnlyList<TaxiBrowseDto> Items, bool HasMore)> SearchByRoutePagedAsync(
        double fromLat, double fromLng, double toLat, double toLng, string? category, int page, int pageSize);
    // status: one of TaxiStatuses.All, or "All" for every non-deleted row.
    Task<(IReadOnlyList<TaxiListing> Items, bool HasMore)> GetAdminPagedAsync(string status, int page, int pageSize);
}

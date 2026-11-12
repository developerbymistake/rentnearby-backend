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
        Guid districtId, Guid? cityId, string? category, int page, int pageSize, bool cityOnly = false, bool localRateOnly = false);
    // need: a trip day's group, km and start point (TaxiLocalRanking); null keeps the plain order.
    Task<(IReadOnlyList<TaxiBrowseDto> Items, bool HasMore)> BrowseLocalPagedAsync(
        Guid districtId, Guid? cityId, string? category, int page, int pageSize, bool cityOnly, bool localRateOnly, RentNearBy.Core.Models.TaxiLocalNeed? need);
    // Taxis with a base pin inside the box of radiusKm around the point, any district, ranked; the caller cuts them by its own rule.
    Task<IReadOnlyList<(TaxiBrowseDto Item, double Lat, double Lng)>> BrowseLocalNearAsync(
        double lat, double lng, double radiusKm, bool localRateOnly, int take, int skip = 0);
    Task<(IReadOnlyList<TaxiBrowseDto> Items, bool HasMore)> SearchByRoutePagedAsync(
        double fromLat, double fromLng, double toLat, double toLng, string? category, int page, int pageSize);
    // status: one of TaxiStatuses.All, or "All" for every non-deleted row.
    Task<(IReadOnlyList<TaxiListing> Items, bool HasMore)> GetAdminPagedAsync(string status, int page, int pageSize);
}

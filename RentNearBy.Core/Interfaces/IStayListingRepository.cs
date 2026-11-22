using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IStayListingRepository : IRepository<StayListing>
{
    // Tracked, for the owner's own writes: Rooms/Photos edits are applied on the loaded collections.
    Task<StayListing?> GetLiveByUserAsync(Guid userId);
    // FB-30: the read-only owner hub read (GET /stays/me): same graph, no tracking, split queries.
    Task<StayListing?> GetLiveByUserReadAsync(Guid userId);
    Task<string?> GetStatusByUserAsync(Guid userId);
    Task<RentNearBy.Core.Models.OwnerListingFacts> GetOwnerFactsAsync(Guid userId);
    // Both include soft-deleted rows.
    Task<bool> HasEverApprovedAsync(Guid userId);
    Task<bool> HasSubmittedHistoryAsync(Guid userId);
    // R-11: a soft-deleted listing of this kind exists (the owner keeps the empty hub after a delete).
    Task<bool> HasDeletedAsync(Guid userId);
    Task<int> TouchAllByUserAsync(Guid userId);
    // !IsDeleted only; the handler applies the Approved-or-owner rule.
    Task<StayListing?> GetByIdWithAllAsync(Guid id);
    // Public share link: only an Approved, not deleted stay resolves.
    Task<StayListing?> GetBySlugWithAllAsync(string slug);
    // Survives IsDeleted so report review still shows what was reported.
    Task<StayListing?> GetByIdForAdminAsync(Guid id);
    // Newest sort pages by opaque keyset cursor (first page: null); price sorts page by offset.
    Task<BrowsePage<StayBrowseDto>> BrowsePagedAsync(Guid districtId, Guid? cityId, string? meals, Guid? stayTypeId, string sortBy, int page, int pageSize, string? cursor);
    // District-free, newest first; same live predicate as browse.
    Task<List<StayBrowseDto>> GetRecentProjectedAsync(int limit);
    // districtId null = no district filter (the caller cuts by its own rule).
    Task<IEnumerable<StayNearbyDto>> GetNearbyAsync(double latitude, double longitude, double radiusKm, Guid? districtId, Guid? stayTypeId, int take = 0, int skip = 0);
    Task<IEnumerable<StayNearbyDto>> GetNearestAsync(double latitude, double longitude, int count, Guid? districtId, Guid? stayTypeId, double? maxKm = null);
    Task<(IReadOnlyList<StayListing> Items, bool HasMore)> GetAdminPagedAsync(string status, int page, int pageSize);
}

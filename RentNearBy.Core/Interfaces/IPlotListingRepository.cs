using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IPlotListingRoomListingRepository : IRepository<PlotListing>
{
    Task<IEnumerable<NearbyPlotListingDto>> GetNearbyAsync(double latitude, double longitude, double radiusKm, Guid districtId);
    Task<IEnumerable<PlotListing>> GetByUserIdAsync(Guid userId);
    // District-free — every user sees the same result, unlike GetAllAsync's district scoping.
    Task<IEnumerable<PlotListing>> GetRecentAsync(int limit);

    // SQL COUNT, never load-then-filter — same reasoning as IRoomRoomListingRepository's.
    Task<int> CountByUserIdAsync(Guid userId);
    Task<(IReadOnlyList<PlotListing> Items, bool HasMore)> GetByUserIdPagedAsync(Guid userId, int page, int pageSize);
    Task<PlotListing?> GetByIdWithPhotosAsync(Guid id);
    Task<PlotListing?> GetByIdWithPhotosForAdminAsync(Guid id);
    Task<(IReadOnlyList<PlotListing> Items, bool HasMore)> GetAllAsync(
        int page, int pageSize,
        string? plotType = null,
        bool? isActive = null,
        Guid? districtId = null,
        Guid? cityId = null);
    Task<(IReadOnlyList<PlotListing> Items, bool HasMore)> GetAllPagedByTypeIdAsync(
        Guid? districtId, Guid? cityId, Guid? plotTypeId, string sortBy, int page, int pageSize);
    Task<IEnumerable<PlotListing>> GetActiveByUserIdAsync(Guid userId);
    Task AddPhotoAsync(PlotPhoto photo);
    void RemovePhoto(PlotPhoto photo);
    Task<IReadOnlyList<PendingDigestListingDto>> GetPendingDigestListingsAsync();
    Task<int> MarkDigestNotifiedAsync(IEnumerable<Guid> ids);
}

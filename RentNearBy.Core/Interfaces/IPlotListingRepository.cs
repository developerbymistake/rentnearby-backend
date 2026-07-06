using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IPlotListingRoomListingRepository : IRepository<PlotListing>
{
    Task<IEnumerable<NearbyPlotListingDto>> GetNearbyAsync(double latitude, double longitude, double radiusKm, Guid cityId);
    Task<IEnumerable<PlotListing>> GetByUserIdAsync(Guid userId);
    Task<(IReadOnlyList<PlotListing> Items, bool HasMore)> GetByUserIdPagedAsync(Guid userId, int page, int pageSize);
    Task<PlotListing?> GetByIdWithPhotosAsync(Guid id);
    Task<(IReadOnlyList<PlotListing> Items, bool HasMore)> GetAllAsync(
        int page, int pageSize,
        string? plotType = null,
        bool? isActive = null,
        Guid? districtId = null,
        Guid? cityId = null);
    Task<IEnumerable<PlotListing>> GetActiveByUserIdAsync(Guid userId);
    Task AddPhotoAsync(PlotPhoto photo);
    void RemovePhoto(PlotPhoto photo);
    Task<IReadOnlyList<PendingDigestListingDto>> GetPendingDigestListingsAsync();
    Task<int> MarkDigestNotifiedAsync(IEnumerable<Guid> ids);
}

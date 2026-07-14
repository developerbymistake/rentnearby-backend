using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IRoomRoomListingRepository : IRepository<RoomListing>
{
    Task<IEnumerable<NearbyListingDto>> GetNearbyAsync(double latitude, double longitude, double radiusKm, Guid districtId);
    Task<IEnumerable<RoomListing>> SearchAsync(Guid? districtId, Guid? roomTypeId, int? priceMin, int? priceMax, int? limit = null);
    Task<(IReadOnlyList<RoomListing> Items, bool HasMore)> SearchPagedAsync(Guid? districtId, Guid? cityId, Guid? roomTypeId, string sortBy, int page, int pageSize);
    Task<IEnumerable<RoomListing>> GetByUserIdAsync(Guid userId);
    Task<IEnumerable<RoomListing>> GetActiveByUserIdAsync(Guid userId);
    Task<(IReadOnlyList<RoomListing> Items, bool HasMore)> GetByUserIdPagedAsync(Guid userId, int page, int pageSize);
    Task<RoomListing?> GetByIdWithPhotosAsync(Guid id);
    Task<RoomListing?> GetByIdWithPhotosForAdminAsync(Guid id);
    Task AddPhotoAsync(RoomPhoto photo);
    void RemovePhoto(RoomPhoto photo);
    Task<IReadOnlyList<PendingDigestListingDto>> GetPendingDigestListingsAsync();
    Task<int> MarkDigestNotifiedAsync(IEnumerable<Guid> ids);
}

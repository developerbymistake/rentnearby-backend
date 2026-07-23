using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IRoomRoomListingRepository : IRepository<RoomListing>
{
    Task<IEnumerable<NearbyListingDto>> GetNearbyAsync(double latitude, double longitude, double radiusKm, Guid districtId);
    Task<IEnumerable<RoomListing>> SearchAsync(Guid? districtId, Guid? roomTypeId, int? priceMin, int? priceMax, int? limit = null);
    // District-free — every user sees the same result, unlike SearchAsync's district scoping.
    Task<IEnumerable<RoomListing>> GetRecentAsync(int limit);
    Task<(IReadOnlyList<RoomListing> Items, bool HasMore)> SearchPagedAsync(Guid? districtId, Guid? cityId, Guid? roomTypeId, string sortBy, int page, int pageSize);
    Task<IEnumerable<RoomListing>> GetByUserIdAsync(Guid userId);

    // SQL COUNT, never load-then-filter — the listing-creation cap check runs on every Add Room
    // attempt and must not repeat GetActiveRoomCountAsync's old load-the-whole-collection mistake.
    Task<int> CountByUserIdAsync(Guid userId);
    Task<IEnumerable<RoomListing>> GetActiveByUserIdAsync(Guid userId);
    Task<(IReadOnlyList<RoomListing> Items, bool HasMore)> GetByUserIdPagedAsync(Guid userId, int page, int pageSize);
    Task<RoomListing?> GetByIdWithPhotosAsync(Guid id);
    Task<RoomListing?> GetByIdWithPhotosForAdminAsync(Guid id);
    Task AddPhotoAsync(RoomPhoto photo);
    void RemovePhoto(RoomPhoto photo);
    Task<IReadOnlyList<PendingDigestListingDto>> GetPendingDigestListingsAsync();
    Task<int> MarkDigestNotifiedAsync(IEnumerable<Guid> ids);
}

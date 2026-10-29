using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IRoomRoomListingRepository : IRepository<RoomListing>
{
    Task<IEnumerable<NearbyListingDto>> GetNearbyAsync(double latitude, double longitude, double radiusKm, Guid districtId);
    Task<IEnumerable<NearbyListingDto>> GetNearestAsync(double latitude, double longitude, int count, Guid districtId);
    // Home rails, projected (no Include, no full entities).
    Task<List<HomeRoomDto>> GetHomeForYouProjectedAsync(Guid districtId, int limit);
    // District-free — every user sees the same result.
    Task<List<HomeRoomDto>> GetRecentProjectedAsync(int limit);
    // Newest sort pages by opaque keyset cursor (first page: null); price sorts page by offset.
    Task<BrowsePage<HomeRoomDto>> SearchPagedAsync(Guid districtId, Guid? cityId, Guid? roomTypeId, string sortBy, int page, int pageSize, string? cursor);
    Task<IEnumerable<RoomListing>> GetByUserIdAsync(Guid userId);

    // Plan slot counts live in ILiveSlotService (CountForCreationAsync / CountLiveAsync).
    Task<IEnumerable<RoomListing>> GetActiveByUserIdAsync(Guid userId);
    Task<(IReadOnlyList<RoomListing> Items, bool HasMore)> GetByUserIdPagedAsync(Guid userId, int page, int pageSize);
    Task<RoomListing?> GetByIdWithPhotosAsync(Guid id);
    Task<RoomListing?> GetByIdWithPhotosForAdminAsync(Guid id);
    // Public (no-auth) by-slug lookup — GET /listings/by-slug/{slug}. Same includes as
    // GetByIdWithPhotosAsync; maps to PublicRoomListingDto, which already excludes OwnerPhone/UserId
    // at the DTO level, so no separate redaction step is needed here.
    Task<RoomListing?> GetBySlugWithPhotosAsync(string slug);
    Task AddPhotoAsync(RoomPhoto photo);
    void RemovePhoto(RoomPhoto photo);
    Task<IReadOnlyList<PendingDigestListingDto>> GetPendingDigestListingsAsync();
    Task<int> MarkDigestNotifiedAsync(IEnumerable<Guid> ids);
}

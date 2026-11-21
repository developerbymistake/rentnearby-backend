using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Core.Interfaces.Owner;

// Lane BE2 (T1). Pure over the rows it is given, no query: the rules and blocks ride on the cached candidate rows.
// Called once after every StayNearbyRooms.Apply of the stay list reads; no checkIn = the rows unchanged except the cache-only
// rules / blocks stripped. Returns the list to send.
public interface IStayDatedPricing
{
    List<StayNearbyDto> Apply(List<StayNearbyDto> items, DateOnly? checkIn, int nights, int? guests, int? rooms, bool groundFloorOnly, DateOnly todayIst);
}

// Lane BE2 (T2). The stay detail and share-link reads: one statement loads the stay's active rules and blocks and fills the dated
// fields of the DTO. Called once per detail read after the DTO is built; no checkIn = nothing changes and no statement runs.
public interface IStayDetailPricing
{
    Task ApplyAsync(StayPublicDto dto, DateOnly? checkIn, int nights, DateOnly todayIst, CancellationToken ct = default);

    Task ApplyAsync(StayPublicSlugDto dto, DateOnly? checkIn, int nights, DateOnly todayIst, CancellationToken ct = default);
}

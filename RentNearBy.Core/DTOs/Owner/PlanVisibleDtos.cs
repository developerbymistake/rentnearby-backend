using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Core.DTOs.Owner;

// Contract 2.4 V1 / V2, AU-9: choose what stays visible on the plan. Rows are never deleted, only hidden.
public sealed class StayPlanVisibleRequest
{
    public List<Guid>? PhotoIds { get; set; }
    public List<Guid>? RoomIds { get; set; }
}

public sealed class TaxiPlanVisibleRequest
{
    public List<Guid>? RouteIds { get; set; }
}

// A photo hidden by the plan (o_st_61 B draws it with a lock).
public sealed record HiddenPhotoDto(Guid Id, string Url, int PhotoOrder);

// /plans/me hidden and usage, the PausedByPlanCount source (M3, AU-21).
public sealed record PlanHiddenDto(int Photos, int RoomTypes, int Routes, int SpecialPrices)
{
    public static readonly PlanHiddenDto None = new(0, 0, 0, 0);
    public int Total => Photos + RoomTypes + Routes + SpecialPrices;
}

public sealed record PlanUsageDto(int Photos, int RoomTypes, int Routes);

public sealed class StayPlanVisibleResponse
{
    public List<StayPhotoDto> Photos { get; set; } = new();
    public List<StayRoomDto> Rooms { get; set; } = new();
    public List<HiddenPhotoDto> HiddenPhotos { get; set; } = new();
    public List<StayRoomDto> HiddenRooms { get; set; } = new();
    public List<MeterDto> Meters { get; set; } = new();
    public int SavesLeftToday { get; set; }
    public bool PaymentEnabled { get; set; }
}

public sealed class TaxiPlanVisibleResponse
{
    public List<TaxiRouteDto> Routes { get; set; } = new();
    public List<TaxiRouteDto> HiddenRoutes { get; set; } = new();
    public List<MeterDto> Meters { get; set; } = new();
    public int SavesLeftToday { get; set; }
    public bool PaymentEnabled { get; set; }
}

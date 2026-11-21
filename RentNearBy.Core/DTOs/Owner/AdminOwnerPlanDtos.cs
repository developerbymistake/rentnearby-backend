namespace RentNearBy.Core.DTOs.Owner;

// Admin shapes of the owner roles work (section 9, AU-24, R-14). The admin form is drawn from the key catalog.
public sealed record AdminPlanKeyDto(
    string Key, string Kind, bool IsSwitch, int Min, int Max, IReadOnlyList<string> ListingKinds, string Label,
    IReadOnlyDictionary<string, string> Labels);

public sealed record AdminPlanValuesDto(Guid PlanId, string FeatureKey, bool IsDefault, IReadOnlyDictionary<string, object> Values);

public sealed record AdminPlanPhotoDto(Guid Id, string Url, int PhotoOrder, bool HiddenByPlan);

public sealed record AdminPlanRoomDto(Guid Id, string RoomName, int PricePerNight, short MaxGuests, string Meals, bool HiddenByPlan);

public sealed record AdminPlanRouteDto(Guid Id, string FromName, string ToName, int Fare, string TripType, bool HiddenByPlan);

// A4: every row of the listing, hidden ones included, each with its flag.
public sealed record AdminPlanRowsDto(List<AdminPlanPhotoDto> Photos, List<AdminPlanRoomDto> Rooms, List<AdminPlanRouteDto> Routes);

public sealed record AdminPlanUsageDto(int Photos, int RoomTypes, int Routes);

public sealed record AdminSpecialPriceReportRowDto(
    Guid RuleId, Guid StayId, string? StayName, DateOnly DateFrom, DateOnly DateTo, string Direction, int Percent, bool BigOff);

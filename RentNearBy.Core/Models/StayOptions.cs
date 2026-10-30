using System.Text.RegularExpressions;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

public static class StayStatuses
{
    public const string Draft = "Draft";
    public const string PendingReview = "PendingReview";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    public static readonly string[] All = [Draft, PendingReview, Approved, Rejected];
}

public static class StayVehicleAccess
{
    public const string CarToGate = "CarToGate";
    public const string ShortWalk = "ShortWalk";
    public const string LongWalk = "LongWalk";

    public static readonly string[] All = [CarToGate, ShortWalk, LongWalk];
}

// Fixed server-side list; the app mirrors it. Stored verbatim as the display name.
public static class StayRoomNames
{
    public const string Standard = "Standard";
    public const string Deluxe = "Deluxe";
    public const string Premium = "Premium";
    public const string Suite = "Suite";
    public const string FamilyRoom = "Family Room";
    public const string Executive = "Executive";
    public const string Dormitory = "Dormitory";

    public static readonly string[] All = [Standard, Deluxe, Premium, Suite, FamilyRoom, Executive, Dormitory];
    public const int MaxLength = 16;
}

public static class StayMeals
{
    public const string RoomOnly = "RoomOnly";
    public const string Breakfast = "Breakfast";
    public const string AllMeals = "AllMeals";

    public static readonly string[] All = [RoomOnly, Breakfast, AllMeals];
}

public static class StayCancellation
{
    public const string Flexible = "Flexible";
    public const string Moderate = "Moderate";
    public const string Strict = "Strict";

    public static readonly string[] All = [Flexible, Moderate, Strict];
}

// Fixed server-side list; the app mirrors it.
public static class StayFacilities
{
    public const string HotWater = "HotWater";
    public const string Heater = "Heater";
    public const string WiFi = "WiFi";
    public const string Parking = "Parking";
    public const string HomeFood = "HomeFood";
    public const string MountainView = "MountainView";

    public static readonly string[] All = [HotWater, Heater, WiFi, Parking, HomeFood, MountainView];
}

public static class StayLimits
{
    public const int MaxRooms = 2;
    public const int MaxPhotos = 5;
    public const int MinPrice = 100;
    public const int MaxPrice = 9_900_000;
}

// Step flags and the setup percentage, always computed from the row (Photos and Rooms must be loaded).
public static class StaySetupRules
{
    public const int Step1Weight = 40;
    public const int RoomsWeight = 30;
    public const int RulesWeight = 20;
    public const int SubmitWeight = 10;

    public static bool Step1Done(StayListing s) =>
        s.Name != null && s.StayTypeId != null && s.Latitude != null && s.Longitude != null
        && s.Address != null && s.DistrictId != null && s.Photos.Count >= 1;

    public static bool RoomsDone(StayListing s) => s.Rooms.Count >= 1;

    public static bool RulesDone(StayListing s) => s.RulesSavedAt != null;

    public static bool Submitted(StayListing s) =>
        s.Status is StayStatuses.PendingReview or StayStatuses.Approved;

    public static int Percent(StayListing s) =>
        (Step1Done(s) ? Step1Weight : 0)
        + (RoomsDone(s) ? RoomsWeight : 0)
        + (RulesDone(s) ? RulesWeight : 0)
        + (Submitted(s) ? SubmitWeight : 0);
}

public static class StayErrorTypes
{
    public const string StayStepLocked = "StayStepLocked";
    public const string StayLocked = "StayLocked";
    public const string StayAlreadyExists = "StayAlreadyExists";
    public const string StayRoomLimit = "StayRoomLimit";
    public const string StayNotApproved = "StayNotApproved";
    public const string StayRegistrationRequired = "StayRegistrationRequired";
    public const string StayRegistrationTypeInUse = "StayRegistrationTypeInUse";
}

// Seeded rows are re-inserted on start when missing, so they can only be switched off, never deleted.
public static class StayRegistrationTypeSeed
{
    private static readonly Regex SeededId = new(@"^e2000000-0000-0000-0000-0000000000[0-9]{2}$");

    public static bool IsSeeded(Guid id) => SeededId.IsMatch(id.ToString());
}

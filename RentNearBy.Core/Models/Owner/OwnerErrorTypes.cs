namespace RentNearBy.Core.Models.Owner;

// Contract appendix A and 13.17: type -> HTTP status and default message. The app switches on the type; old builds toast the message.
public static class OwnerErrorTypes
{
    public const string ListingNotFound = "ListingNotFound";
    public const string PlanPhotoLimit = "PlanPhotoLimit";
    public const string PlanRoomTypeLimit = "PlanRoomTypeLimit";
    public const string TaxiRouteLimit = "TaxiRouteLimit";
    public const string StayJobLimit = "StayJobLimit";
    public const string CalendarEntryLimit = "CalendarEntryLimit";
    public const string CalendarOutsideWindow = "CalendarOutsideWindow";
    public const string EntryNotManual = "EntryNotManual";
    public const string EntryEnded = "EntryEnded";
    public const string BlockedRangeLimit = "BlockedRangeLimit";
    public const string BlockedRangeOverlap = "BlockedRangeOverlap";
    public const string BlockedRangeNeedsConfirm = "BlockedRangeNeedsConfirm";
    public const string BlockedRangeInvalid = "BlockedRangeInvalid";
    public const string SpecialPriceLimit = "SpecialPriceLimit";
    public const string SpecialPriceOverlap = "SpecialPriceOverlap";
    public const string SpecialPriceSavesLimit = "SpecialPriceSavesLimit";
    public const string SpecialPriceInvalid = "SpecialPriceInvalid";
    public const string PlanVisibleLimit = "PlanVisibleLimit";
    public const string PlanVisibleInvalid = "PlanVisibleInvalid";
    public const string PlanVisibleNotNeeded = "PlanVisibleNotNeeded";
    public const string PlanFeatureLocked = "PlanFeatureLocked";
    public const string MoneyNoteInvalid = "MoneyNoteInvalid";
    public const string PhoneNotAllowed = "PhoneNotAllowed";
    public const string StaleEdit = "StaleEdit";
    public const string IdTaken = "IdTaken";
    public const string StayFullOnDates = "StayFullOnDates";
    public const string ChangeTouchesBlock = "ChangeTouchesBlock";
    public const string RoomTypeDuplicate = "RoomTypeDuplicate";
    public const string StayNeedsPhoto = "StayNeedsPhoto";
    public const string StayEditGone = "StayEditGone";
    public const string StayEditChanged = "StayEditChanged";
    public const string PlanValueBelowFree = "PlanValueBelowFree";
    public const string AnnounceTooSoon = "AnnounceTooSoon";
    public const string RoleMismatch = "RoleMismatch";
    public const string TooManyRequests = "TooManyRequests";
    public const string BadRequest = "BadRequest";
    public const string StayLocked = "StayLocked";
    public const string PaymentAlreadyOn = "PaymentAlreadyOn";

    // D-07 / FB-04: an ended manual entry keeps only its money note and Delete.
    public const string EntryEndedStay = "This booking has passed. You can still edit the amounts.";
    public const string EntryEndedTaxi = "This trip has passed. You can still edit the amounts.";
    public const string BlockEnded = "This block has ended.";

    public static string EntryEndedMessage(string listingKind) => listingKind == ListingKinds.Taxi ? EntryEndedTaxi : EntryEndedStay;

    // Extra fixed lines of SpecialPriceInvalid (contract 2.3, doc 8.8).
    public const string SpecialPriceTooLong = "A special price can cover up to 60 nights.";
    public const string SpecialPriceStartTooLate = "Start date must be within 180 days.";
    public const string SpecialPriceStartPast = "Start date must be today or later.";
    public const string SpecialPricePercentLower = "Pick a percent between 5 and 50.";
    public const string SpecialPricePercentHigher = "Pick a percent between 5 and 100.";

    private static readonly Dictionary<string, (int Status, string Message)> Table = new()
    {
        [ListingNotFound] = (404, "Set up your listing first."),
        [PlanPhotoLimit] = (409, "Free plan allows 2 photos."),
        [PlanRoomTypeLimit] = (409, "2 room types on Free."),
        [TaxiRouteLimit] = (409, "1 route on Free."),
        [StayJobLimit] = (409, "Your plan allows 1 active job. Close one or upgrade."),
        [CalendarEntryLimit] = (409, "You have 4 active entries."),
        [CalendarOutsideWindow] = (409, "Pick a date inside your calendar."),
        [EntryNotManual] = (409, "A booking from the app cannot be changed here."),
        [EntryEnded] = (409, EntryEndedStay),
        [BlockedRangeLimit] = (409, "Free allows 1 blocked range."),
        [BlockedRangeOverlap] = (409, "These days are already blocked."),
        [BlockedRangeNeedsConfirm] = (409, "1 confirmed booking is inside these days. It stays confirmed."),
        [BlockedRangeInvalid] = (400, "Block 1 to 90 nights, starting within 180 days."),
        [SpecialPriceLimit] = (409, "1 special price on Free."),
        [SpecialPriceOverlap] = (409, "These dates already have a special price."),
        [SpecialPriceSavesLimit] = (409, "You can change special prices 3 times a day. Try again tomorrow."),
        [SpecialPriceInvalid] = (400, SpecialPriceTooLong),
        [PlanVisibleLimit] = (409, "You can change this 3 times a day."),
        [PlanVisibleInvalid] = (400, "Pick exactly what your plan allows."),
        [PlanVisibleNotNeeded] = (409, "Nothing is hidden."),
        [PlanFeatureLocked] = (403, "This is part of Plus."),
        [MoneyNoteInvalid] = (400, MoneyNoteMath.AdvanceAboveFinal),
        [PhoneNotAllowed] = (400, "Do not add phone numbers."),
        [StaleEdit] = (409, "This changed on another phone. Pull to refresh."),
        [IdTaken] = (409, "Please try again."),
        [StayFullOnDates] = (409, "This stay is full on your dates."),
        [ChangeTouchesBlock] = (409, "These days are blocked by you. Accept anyway?"),
        [RoomTypeDuplicate] = (400, "You already have a room of this type. Pick another type."),
        [StayNeedsPhoto] = (409, "Keep at least one photo."),
        [StayEditGone] = (409, "This change is no longer waiting."),
        [StayEditChanged] = (409, "The owner changed it again. Reload."),
        [PlanValueBelowFree] = (400, "Plus must be the same or higher than Free."),
        [AnnounceTooSoon] = (400, "Pick a date at least 7 days ahead."),
        [RoleMismatch] = (403, "This is not available for your role."),
        [TooManyRequests] = (429, "Too many tries. Please wait."),
        [BadRequest] = (400, "Invalid request."),
        [StayLocked] = (409, "Your stay is under review."),
        [PaymentAlreadyOn] = (400, "Payments are already on."),
    };

    public static IReadOnlyCollection<string> All => Table.Keys;

    public static int StatusOf(string type) => Table.TryGetValue(type, out var e) ? e.Status : 400;

    public static string MessageOf(string type) => Table.TryGetValue(type, out var e) ? e.Message : "Invalid request.";

    // The limit line of 3.2 / R-6: the Free line while an upgrade is possible, else the "up to N" line (Plus at the cap, payment OFF).
    public static string LimitMessage(string type, int limit, bool canUpgrade) => type switch
    {
        PlanPhotoLimit => canUpgrade ? $"Free plan allows {Count(limit, "photo")}." : $"You can add up to {Count(limit, "photo")}.",
        PlanRoomTypeLimit => canUpgrade ? $"{Count(limit, "room type")} on Free." : $"You can add up to {Count(limit, "room type")}.",
        TaxiRouteLimit => canUpgrade ? $"{Count(limit, "route")} on Free." : $"You can add up to {Count(limit, "route")}.",
        StayJobLimit => canUpgrade
            ? $"Your plan allows {Count(limit, "active job")}. Close one or upgrade."
            : $"You can have up to {Count(limit, "active job")}. Close one to post another.",
        CalendarEntryLimit => canUpgrade ? $"You have {Count(limit, "active entry", "active entries")}." : $"You can add up to {Count(limit, "entry", "entries")}.",
        BlockedRangeLimit => canUpgrade ? $"Free allows {Count(limit, "blocked range")}." : $"You can add up to {Count(limit, "blocked range")}.",
        SpecialPriceLimit => canUpgrade ? $"{Count(limit, "special price")} on Free." : $"You can add up to {Count(limit, "special price")}.",
        _ => MessageOf(type),
    };

    public static string RoomTypeDuplicateMessage(string roomName) => $"You already have a {roomName} room. Pick another type.";

    private static string Count(int n, string one, string? many = null) => n == 1 ? $"1 {one}" : $"{n} {many ?? one + "s"}";
}

namespace RentNearBy.Core.Models.Owner;

// Blocked ranges of a stay (contract 2.2, doc 6.1). Dates are inclusive nights. Pure.
public static class BlockedRangeRules
{
    public const string ReasonTooLong = "Keep the reason under 60 characters.";

    public static int Nights(DateOnly from, DateOnly to) => to.DayNumber - from.DayNumber + 1;

    // null = valid. 1 to 90 nights, starting today .. today + 180. An edit of a block that already started may keep its start.
    public static CalendarRuleError? Validate(DateOnly from, DateOnly to, string? reason, DateOnly today, DateOnly? keptStart = null)
    {
        var nights = Nights(from, to);
        var startOk = (from >= today || from == keptStart) && from <= today.AddDays(OwnerLimits.BlockStartWithinDays);
        if (nights < 1 || nights > OwnerLimits.BlockMaxNights || !startOk || to < today)
            return new CalendarRuleError(OwnerErrorTypes.BlockedRangeInvalid, OwnerErrorTypes.MessageOf(OwnerErrorTypes.BlockedRangeInvalid));
        if (reason is { Length: > OwnerLimits.ReasonMax }) return new CalendarRuleError(OwnerErrorTypes.BadRequest, ReasonTooLong);
        if (PhoneGuard.HasPhone(reason))
            return new CalendarRuleError(OwnerErrorTypes.PhoneNotAllowed, OwnerErrorTypes.MessageOf(OwnerErrorTypes.PhoneNotAllowed));
        return null;
    }

    public static bool Overlaps(DateOnly aFrom, DateOnly aTo, DateOnly bFrom, DateOnly bTo) => aFrom <= bTo && bFrom <= aTo;

    // A booking's nights are CheckIn .. CheckOut - 1; it is inside a block when one of them is a blocked night.
    public static bool BookingInside(DateOnly checkIn, DateOnly checkOut, DateOnly blockFrom, DateOnly blockTo)
        => checkIn <= blockTo && checkOut > blockFrom;

    public static string NeedsConfirmMessage(int confirmedInside)
        => confirmedInside == 1
            ? OwnerErrorTypes.MessageOf(OwnerErrorTypes.BlockedRangeNeedsConfirm)
            : $"{confirmedInside} confirmed bookings are inside these days. They stay confirmed.";
}

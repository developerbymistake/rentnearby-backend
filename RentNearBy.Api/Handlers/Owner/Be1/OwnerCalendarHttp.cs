using System.Globalization;
using System.Security.Claims;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.Owner.Be1;

// Kind-agnostic HTTP steps of the two owner calendars: caller, owner context (404 without a live listing) and the refusal shape.
public static class OwnerCalendarHttp
{
    public const string TimeInvalid = "Pick a valid time.";
    public const string DatesMissing = "Pick the dates.";

    public static async Task<(OwnerContext? Ctx, IResult? Fail)> ContextAsync(Guid userId, string kind, OwnerCalendarReader reader, CancellationToken ct)
    {
        var ctx = await reader.ContextAsync(userId, kind, ct);
        return ctx == null ? (null, OwnerHttp.ListingNotFound()) : (ctx, null);
    }

    public static IResult Error<TEntry, TBlock>(Be1Error error, Func<CalendarEntryRow, TEntry> entry, Func<BlockedRangeRow, TBlock>? block = null)
    {
        if (error.Type == Be1ErrorTypes.NotFound) return NotFoundResponse(error.Message);
        if (error.Type == OwnerErrorTypes.StaleEdit)
        {
            if (error.CurrentEntry is { } e) return OwnerHttp.StaleEdit(entry(e));
            if (error.CurrentBlock is { } b && block != null) return OwnerHttp.StaleEdit(block(b));
        }
        return OwnerHttp.Refuse(error.Type, error.Message, error.Data);
    }

    public static IResult Error<TEntry>(Be1Error error, Func<CalendarEntryRow, TEntry> entry) => Error<TEntry, object>(error, entry);

    // "HH:mm" (or "HH:mm:ss"); null or empty = no time.
    public static bool TryTime(string? text, out TimeOnly? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!TimeOnly.TryParseExact(text.Trim(), ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return false;
        time = t;
        return true;
    }

    public static MoneyNote Money(int? finalAmount, int? advanceReceived, bool paidInFull) => new(finalAmount, advanceReceived, paidInFull);

    public static bool TryCaller(ClaimsPrincipal principal, out Guid userId) => OwnerHttp.TryOwner(principal, out userId);
}

using System.Globalization;
using System.Text.Json;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// Kept = r shrunk to the nights before (or, with none before, after) the new range; KeptAfter = a clone for the nights
// after it when both sides remain; Cancelled = the nights the new booking takes (r itself when nothing remains).
public sealed record TripReplacePlan(TripEnquiry? Kept, TripEnquiry? KeptAfter, TripEnquiry Cancelled);

// Replace works per night (same idea as TripProblemSplit for D2): only the nights the new booking takes are cancelled.
public static class TripReplaceSplit
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static TripReplacePlan Plan(TripEnquiry r, DateOnly checkIn, DateOnly checkOut, Guid callerId, DateTime nowUtc)
    {
        if (r.CheckIn is not { } rIn || r.CheckOut is not { } rOut) return new(null, null, CancelWhole(r, callerId, nowUtc));
        var cutIn = rIn > checkIn ? rIn : checkIn;
        var cutOut = rOut < checkOut ? rOut : checkOut;
        var before = Math.Max(0, cutIn.DayNumber - rIn.DayNumber);
        var after = Math.Max(0, rOut.DayNumber - cutOut.DayNumber);
        if (cutIn >= cutOut || (before == 0 && after == 0)) return new(null, null, CancelWhole(r, callerId, nowUtc));

        var total = rOut.DayNumber - rIn.DayNumber;
        var beforeTotal = r.AgreedTotal is { } a1 ? Share(a1, before, total) : (int?)null;
        var afterTotal = r.AgreedTotal is { } a2 ? Share(a2, after, total) : (int?)null;

        var cancelled = CloneFor(r, nowUtc);
        Move(cancelled, r, rIn, cutIn, cutOut, r.AgreedTotal is { } all ? all - (beforeTotal ?? 0) - (afterTotal ?? 0) : null);
        cancelled.Status = TripEnquiryStatuses.CancelledByTraveller;
        cancelled.CancelledAt = nowUtc;
        cancelled.CancelledByUserId = callerId;

        TripEnquiry? keptAfter = null;
        if (before > 0 && after > 0)
        {
            keptAfter = CloneFor(r, nowUtc);
            keptAfter.Status = r.Status;
            keptAfter.ConfirmedByTravellerAt = r.ConfirmedByTravellerAt;
            keptAfter.ConfirmedByOwnerAt = r.ConfirmedByOwnerAt;
            Move(keptAfter, r, rIn, cutOut, rOut, afterTotal);
        }

        if (before > 0) Move(r, r, rIn, rIn, cutIn, beforeTotal);
        else Move(r, r, rIn, cutOut, rOut, afterTotal);
        r.UpdatedAt = nowUtc;
        return new(r, keptAfter, cancelled);
    }

    // "the night of 05 Nov", "2 nights from 05 Nov".
    public static string NightsText(DateOnly? checkIn, DateOnly? checkOut)
    {
        if (checkIn is not { } a) return "your dates";
        var n = TripExecutionRules.Nights(checkIn, checkOut);
        var day = a.ToString("dd MMM", Inv);
        return n <= 1 ? $"the night of {day}" : $"{n} nights from {day}";
    }

    private static TripEnquiry CancelWhole(TripEnquiry r, Guid callerId, DateTime nowUtc)
    {
        r.Status = TripEnquiryStatuses.CancelledByTraveller;
        r.CancelledAt = nowUtc;
        r.CancelledByUserId = callerId;
        r.UpdatedAt = nowUtc;
        return r;
    }

    // row takes [from, to) of source (whose check-in is sourceIn): day number, total and context follow.
    private static void Move(TripEnquiry row, TripEnquiry source, DateOnly sourceIn, DateOnly from, DateOnly to, int? total)
    {
        var sourceContext = source.ContextJson;
        row.Day = source.Day is { } d ? (short)(d + (from.DayNumber - sourceIn.DayNumber)) : null;
        row.CheckIn = from;
        row.CheckOut = to;
        row.AgreedTotal = total;
        row.ContextJson = RebuildContext(sourceContext, from, to);
    }

    private static string RebuildContext(string json, DateOnly from, DateOnly to)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            var ctx = JsonSerializer.Deserialize<TripEnquiryContext>(json, TripJson.Options);
            if (ctx == null) return json;
            ctx.Days = ctx.Days.Where(d => d.Date is not { } x || (x >= from && x < to)).ToList();
            ctx.Text = TripEnquiryText.Build(ctx, from, to, to.DayNumber - from.DayNumber > 1);
            return JsonSerializer.Serialize(ctx, TripJson.Options);
        }
        catch (JsonException) { return json; }
    }

    private static int Share(int amount, int part, int whole) => (int)Math.Round((double)amount * part / whole, MidpointRounding.AwayFromZero);

    private static TripEnquiry CloneFor(TripEnquiry e, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        TripId = e.TripId,
        TravellerUserId = e.TravellerUserId,
        OwnerUserId = e.OwnerUserId,
        Kind = e.Kind,
        ListingId = e.ListingId,
        ListingName = e.ListingName,
        ListingLatitude = e.ListingLatitude,
        ListingLongitude = e.ListingLongitude,
        ConversationId = e.ConversationId,
        ContextJson = e.ContextJson,
        ExpiresAt = e.ExpiresAt,
        IsUrgent = e.IsUrgent,
        AdvanceStatus = TripAdvanceStatuses.None,
        ListedRateSnapshot = e.ListedRateSnapshot,
        CreatedAt = nowUtc,
        UpdatedAt = nowUtc,
    };
}

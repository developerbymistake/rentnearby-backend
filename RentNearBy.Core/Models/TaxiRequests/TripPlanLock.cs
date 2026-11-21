using RentNearBy.Core.DTOs.TaxiRequests;

namespace RentNearBy.Core.Models.TaxiRequests;

// A refusal the API writes as is (status, error.type, message, data).
public sealed record TripLockRefusal(int StatusCode, string ErrorType, string Message, PlanLockedData? Data);

// Decisions 14, 20, 23c and contract section 4: while a taxi request or booking, or a booked stay, of the trip is active its structure is frozen.
public static class TripPlanLock
{
    // Structural = legs (city keys, order), days (count, order, leg, rest flag), spots of a day (ids, order), start date,
    // adults, kids and (taxiMark true) the taxi mark. Name, guide, steps, stay marks and dismissed rows are not. Both plans normalised.
    public static bool IsStructuralChange(TripPlan stored, TripPlan incoming, bool taxiMark = true)
    {
        if (!SameLegs(stored.Legs, incoming.Legs)) return true;
        if (!SameDays(stored.Days, incoming.Days)) return true;
        var a = stored.Settings;
        var b = incoming.Settings;
        if (a?.StartDate != b?.StartDate || a?.Adults != b?.Adults || a?.Kids != b?.Kids) return true;
        return taxiMark && stored.TaxiMark?.Status != incoming.TaxiMark?.Status;
    }

    public static string MessageFor(string reason) => PlanLockReasons.MessageFor(reason);

    public static string DeleteMessage => PlanLockReasons.DeleteMessage;

    // null = the save may go on (not locked, or nothing structural changed). Under a stay lock the taxi mark stays free.
    public static TripLockRefusal? UpdateRefusal(string? lockReason, Guid? requestId, TripPlan stored, TripPlan incoming, Guid? enquiryId = null)
        => lockReason is { } reason && IsStructuralChange(stored, incoming, taxiMark: PlanLockReasons.IsTaxi(reason))
            ? new TripLockRefusal(409, TaxiRequestErrorTypes.PlanLocked, MessageFor(reason), Data(reason, requestId, enquiryId))
            : null;

    public static TripLockRefusal? DeleteRefusal(string? lockReason, Guid? requestId, Guid? enquiryId = null)
        => lockReason is { } reason
            ? new TripLockRefusal(409, TaxiRequestErrorTypes.PlanLocked, PlanLockReasons.DeleteMessageFor(reason), Data(reason, requestId, enquiryId))
            : null;

    // P25, contract section 1 exception 2: old local taxi writers stop while a taxi request of the trip is active.
    public static TripLockRefusal? LegacyTaxiBlock(bool hasActiveRequest)
        => hasActiveRequest
            ? new TripLockRefusal(409, TaxiRequestErrorTypes.TaxiRequestActive,
                TaxiRequestErrorTypes.Messages[TaxiRequestErrorTypes.TaxiRequestActive], null)
            : null;

    private static PlanLockedData Data(string reason, Guid? requestId, Guid? enquiryId)
        => new() { Reason = reason, RequestId = requestId, EnquiryId = enquiryId };

    private static bool SameLegs(List<TripCityLeg>? a, List<TripCityLeg>? b)
    {
        var x = a ?? [];
        var y = b ?? [];
        if (x.Count != y.Count) return false;
        for (var i = 0; i < x.Count; i++)
            if (x[i]?.CityKey != y[i]?.CityKey) return false;
        return true;
    }

    private static bool SameDays(List<TripDay>? a, List<TripDay>? b)
    {
        var x = a ?? [];
        var y = b ?? [];
        if (x.Count != y.Count) return false;
        for (var i = 0; i < x.Count; i++)
            if (!SameDay(x[i], y[i])) return false;
        return true;
    }

    // The one day number whose places changed when nothing else did (same legs, day count and settings); else null.
    public static int? OnlyChangedDay(TripPlan stored, TripPlan incoming)
    {
        if (!SameLegs(stored.Legs, incoming.Legs)) return null;
        var a = stored.Settings;
        var b = incoming.Settings;
        if (a?.StartDate != b?.StartDate || a?.Adults != b?.Adults || a?.Kids != b?.Kids) return null;
        var x = stored.Days ?? [];
        var y = incoming.Days ?? [];
        if (x.Count != y.Count) return null;
        int? changed = null;
        for (var i = 0; i < x.Count; i++)
        {
            if (SameDay(x[i], y[i])) continue;
            if (changed is not null || x[i] == null || y[i] == null || x[i].Day != y[i].Day) return null;
            changed = y[i].Day;
        }
        return changed is > 0 ? changed : null;
    }

    private static bool SameDay(TripDay? p, TripDay? q)
    {
        if (p == null || q == null) return p == q;
        if (p.Day != q.Day || (p.Leg ?? 1) != (q.Leg ?? 1) || (p.RestDay == true) != (q.RestDay == true)) return false;
        return SpotIds(p).SequenceEqual(SpotIds(q), StringComparer.Ordinal);
    }

    private static IEnumerable<string> SpotIds(TripDay day) => (day.Spots ?? []).Select(s => s?.Id ?? string.Empty);
}

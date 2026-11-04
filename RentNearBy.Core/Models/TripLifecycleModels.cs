using System.Text.Json;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

public static class TripProblemKinds
{
    public const string DriverNoShow = "DriverNoShow", HostNoRoom = "HostNoRoom", RoadClosed = "RoadClosed", Other = "Other";
    public static readonly string[] All = [DriverNoShow, HostNoRoom, RoadClosed, Other];

    public static string? FailReasonOf(string kind) => kind switch
    {
        DriverNoShow => TripFailReasons.ProblemDriverNoShow,
        HostNoRoom => TripFailReasons.ProblemHostNoRoom,
        _ => null,
    };

    public static bool MarksBooking(string kind) => kind is DriverNoShow or HostNoRoom;

    public static string ExpectedKind(string problemKind) => problemKind == DriverNoShow ? TripEnquiryKinds.Taxi : TripEnquiryKinds.Stay;
}

public static class TripBookingStates
{
    public const string Draft = "draft", Confirming = "confirming", Ready = "ready", Completed = "completed";

    // DESIGN 2.6. A red row that no settled row of the same kind replaces counts as Confirming.
    public static string Compute(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> enquiries)
    {
        if (trip.Status == TripStatuses.Completed) return Completed;
        var start = trip.StartDate ?? plan.Settings?.StartDate;
        var spots = plan.Days.Sum(d => d.Spots.Count);
        if (trip.Status != TripStatuses.Planned || start == null || spots == 0) return Draft;

        var map = TripBookingMap.Build(plan, enquiries);
        if (map.HasOpen || map.HasUnreplacedRed) return Confirming;
        if (!map.StayAny.IsSubsetOf(map.StaySettled) || !map.TaxiAny.IsSubsetOf(map.TaxiSettled)) return Confirming;
        return Ready;
    }

    // An advance counts as open only while the booking is still live: cancel, problem and replace never reset AdvanceStatus.
    public static bool IsOpenForCompletion(TripEnquiry e) =>
        e.Status is TripEnquiryStatuses.Asked or TripEnquiryStatuses.Offered or TripEnquiryStatuses.Countered or TripEnquiryStatuses.FareSent
        || (e.Status == TripEnquiryStatuses.ConfirmedByBoth && e.AdvanceStatus is TripAdvanceStatuses.Pending or TripAdvanceStatuses.ProofSent);

    // Green or blue.
    public static bool IsSettled(TripEnquiry e) => e.Status is TripEnquiryStatuses.ConfirmedByBoth or TripEnquiryStatuses.BookedByTraveller;
}

public sealed record TripBookingCounts(int StaysConfirmed, int StaysTotal, int TaxisConfirmed, int TaxisTotal, int AgreedTotal);

// Night (stay) and day (taxi) numbers 1-based, as in the plan.
internal sealed class TripBookingMap
{
    public readonly HashSet<int> StayAny = new(), StaySettled = new(), TaxiAny = new(), TaxiSettled = new(), TaxiBoth = new();
    public bool HasOpen, HasUnreplacedRed;
    public int OpenStays, OpenTaxis;

    private static bool IsRed(TripEnquiry e) => e.Status is TripEnquiryStatuses.NotAvailable or TripEnquiryStatuses.Expired
        or TripEnquiryStatuses.Declined or TripEnquiryStatuses.Failed
        or TripEnquiryStatuses.CancelledByOwner or TripEnquiryStatuses.CancelledByTraveller;

    public static TripBookingMap Build(TripPlan plan, IReadOnlyList<TripEnquiry> enquiries)
    {
        var map = new TripBookingMap();
        var count = plan.Days.Count;
        var start = plan.Settings?.StartDate;

        foreach (var day in plan.Days)
        {
            if (day.Day >= 1 && day.Day < count && day.Stay != null)
            {
                map.StayAny.Add(day.Day);
                if (day.Stay.IsOwn == true) map.StaySettled.Add(day.Day);
            }
            if (day.Day >= 1 && day.Day <= count && day.Taxi != null)
            {
                map.TaxiAny.Add(day.Day);
                if (day.Taxi.IsOwn == true || day.Taxi.Booking != null) map.TaxiSettled.Add(day.Day);
            }
        }

        var live = enquiries.Where(e => e.Status != TripEnquiryStatuses.Closed).ToList();
        foreach (var e in live)
        {
            var taxi = e.Kind == TripEnquiryKinds.Taxi;
            var days = DaysOf(e, start, count);
            (taxi ? map.TaxiAny : map.StayAny).UnionWith(days);
            if (TripBookingStates.IsSettled(e))
            {
                (taxi ? map.TaxiSettled : map.StaySettled).UnionWith(days);
                if (taxi && e.Status == TripEnquiryStatuses.ConfirmedByBoth) map.TaxiBoth.UnionWith(days);
            }
            if (TripBookingStates.IsOpenForCompletion(e))
            {
                map.HasOpen = true;
                if (taxi) map.OpenTaxis++; else map.OpenStays++;
            }
        }

        foreach (var e in live.Where(IsRed))
        {
            var settled = e.Kind == TripEnquiryKinds.Taxi ? map.TaxiSettled : map.StaySettled;
            if (!DaysOf(e, start, count).All(settled.Contains)) map.HasUnreplacedRed = true;
        }
        return map;
    }

    public static List<int> DaysOf(TripEnquiry e, DateOnly? start, int dayCount)
    {
        var taxi = e.Kind == TripEnquiryKinds.Taxi;
        var max = taxi ? dayCount : dayCount - 1;
        var days = new List<int>();

        if (taxi && !string.IsNullOrWhiteSpace(e.DaysJson))
        {
            try { days.AddRange(JsonSerializer.Deserialize<int[]>(e.DaysJson) ?? []); }
            catch (JsonException) { }
        }
        if (days.Count == 0 && start is { } s && e.CheckIn is { } a)
        {
            var last = e.CheckOut?.AddDays(-1) ?? a;
            for (var d = a; d <= last; d = d.AddDays(1)) days.Add(d.DayNumber - s.DayNumber + 1);
        }
        if (days.Count == 0 && e.Day is { } one) days.Add(one);
        return days.Where(d => d >= 1 && d <= max).Distinct().ToList();
    }
}

public static class TripBookingSummary
{
    public static TripBookingCounts Count(TripPlan plan, IReadOnlyList<TripEnquiry> enquiries)
    {
        var map = TripBookingMap.Build(plan, enquiries);
        var total = enquiries
            .Where(e => TripEnquiryStatuses.IsBooked(e.Status))
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .Sum(e => (e.Kind == TripEnquiryKinds.Taxi ? e.AgreedFare : e.AgreedTotal) ?? 0);
        return new TripBookingCounts(map.StaySettled.Count, map.StayAny.Count, map.TaxiSettled.Count, map.TaxiAny.Count, total);
    }
}

public static class TripReminderRules
{
    public const int NoReplyHours = 24, HostNotConfirmedHours = 24, WindowHours = 48;
    public const int DaysBeforeStart = 3, StartsInHourIst = 9, TodayHourIst = 7, MarkCompleteHourIst = 10, AutoCompleteDays = 7;

    public static DateTime StartsInDueUtc(DateOnly start) => TripExecutionRules.IstMidnightUtc(start.AddDays(-DaysBeforeStart)).AddHours(StartsInHourIst);
    public static DateTime TodayDueUtc(DateOnly start) => TripExecutionRules.IstMidnightUtc(start).AddHours(TodayHourIst);
    public static DateTime MarkCompleteDueUtc(DateOnly end) => TripExecutionRules.IstMidnightUtc(end.AddDays(1)).AddHours(MarkCompleteHourIst);

    // A reminder is attempted only inside [due, due + 48 h).
    public static bool InWindow(DateTime dueUtc, DateTime nowUtc) => nowUtc >= dueUtc && nowUtc < dueUtc.AddHours(WindowHours);

    public static bool AutoCompleteDue(DateOnly end, DateOnly todayIst) => end.AddDays(AutoCompleteDays) <= todayIst;
    public static bool IsRunning(Trip t, DateOnly todayIst) => t.StartDate is { } s && t.EndDate is { } e && s <= todayIst && todayIst <= e;
    public static bool CanComplete(Trip t, DateOnly todayIst) => t.StartDate != null && t.EndDate is { } e && todayIst >= e;
    public static bool ProblemAllowed(Trip t, DateOnly todayIst) => t.StartDate is { } s && t.EndDate is { } e && s <= todayIst && todayIst <= e.AddDays(1);

    public static string NoReplyKey(Guid enquiryId) => $"noreply:{enquiryId}";
    public static string HostNotConfirmedKey(Guid enquiryId) => $"hostnc:{enquiryId}";
    public static string StartsInKey(Guid tripId, DateOnly start) => $"d3:{tripId}:{start:yyyyMMdd}";
    public static string TodayKey(Guid tripId, DateOnly start) => $"d0:{tripId}:{start:yyyyMMdd}";
    public static string MarkCompleteKey(Guid tripId, DateOnly end) => $"done:{tripId}:{end:yyyyMMdd}";

    // "Stay confirmed. Taxi is booked by you." | "1 stay enquiry waiting. No taxi yet."
    public static string StartsInSummary(TripPlan plan, IReadOnlyList<TripEnquiry> enquiries)
    {
        var map = TripBookingMap.Build(plan, enquiries);
        string stay;
        if (map.OpenStays > 0) stay = $"{map.OpenStays} stay {(map.OpenStays == 1 ? "enquiry" : "enquiries")} waiting.";
        else if (map.StayAny.Count > 0 && map.StayAny.IsSubsetOf(map.StaySettled)) stay = "Stay confirmed.";
        else stay = "No stay yet.";

        string taxi;
        if (map.OpenTaxis > 0) taxi = $"{map.OpenTaxis} taxi {(map.OpenTaxis == 1 ? "enquiry" : "enquiries")} waiting.";
        else if (map.TaxiAny.Count > 0 && map.TaxiAny.IsSubsetOf(map.TaxiSettled))
            taxi = map.TaxiAny.IsSubsetOf(map.TaxiBoth) ? "Taxi confirmed." : "Taxi is booked by you.";
        else taxi = "No taxi yet.";
        return $"{stay} {taxi}";
    }

    // "Pickup 8:30 am. Tap for the driver's number." when a booked taxi has a pickup time, else "Tap for your bookings."
    public static string TodayBody(IReadOnlyList<TripEnquiry> enquiries)
    {
        var time = enquiries
            .Where(e => e.Kind == TripEnquiryKinds.Taxi && TripEnquiryStatuses.IsBooked(e.Status))
            .OrderBy(e => e.CheckIn)
            .Select(e => TripEnquiryText.TimeText(e.PickupTime))
            .FirstOrDefault(t => t != null);
        return time != null ? $"Pickup {time}. Tap for the driver's number." : "Tap for your bookings.";
    }

    // Every non-last day has a settled or own stay; every day with a taxi choice is settled; nothing open or unreplaced red.
    public static bool AllSettled(TripPlan plan, IReadOnlyList<TripEnquiry> enquiries)
    {
        var map = TripBookingMap.Build(plan, enquiries);
        if (map.HasOpen || map.HasUnreplacedRed) return false;
        for (var d = 1; d < plan.Days.Count; d++)
            if (!map.StaySettled.Contains(d)) return false;
        return map.TaxiAny.IsSubsetOf(map.TaxiSettled);
    }
}

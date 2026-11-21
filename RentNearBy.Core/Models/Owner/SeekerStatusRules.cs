namespace RentNearBy.Core.Models.Owner;

public static class SeekerTripStates
{
    public const string None = "None";
    public const string InProgress = "InProgress";
    public const string OnTrip = "OnTrip";

    // OnTrip = today inside [start, end]; else InProgress (a trip without dates is in progress).
    public static string Of(DateOnly? start, DateOnly? end, DateOnly todayIst)
        => start is { } s && todayIst >= s && (end is null || todayIst <= end) ? OnTrip : InProgress;
}

public static class SeekerStatusKinds
{
    public const string Reply = "Reply";
    public const string Confirmed = "Confirmed";
}

public static class SeekerNextStepKinds
{
    public const string Stay = "Stay";
    public const string Taxi = "Taxi";
    public const string Journey = "Journey";
    public const string Plan = "Plan";

    public static string Of(string rowKind) => rowKind switch
    {
        TripRowKinds.Stay => Stay,
        TripRowKinds.Taxi => Taxi,
        TripRowKinds.GettingThere or TripRowKinds.ComingBack or TripRowKinds.Between => Journey,
        _ => Plan,
    };
}

// One stay enquiry of the chosen trip, as the status line needs it.
public sealed record SeekerEnquiryFact(Guid Id, string Status, string ListingName, DateTime UpdatedAt);

public sealed record SeekerStatus(string Kind, int Count, string ListingName, Guid? EnquiryId, string Key);

// Doc 14a / AU-20: Reply (hosts answered with a price, status Offered) wins over Confirmed (newest ConfirmedByBoth).
// The key changes whenever the facts change; the app hides the line once the user opened that key (no server state).
public static class SeekerStatusRules
{
    public static SeekerStatus? Of(IReadOnlyList<SeekerEnquiryFact> stayEnquiries)
    {
        var offered = stayEnquiries.Where(e => e.Status == TripEnquiryStatuses.Offered)
            .OrderByDescending(e => e.UpdatedAt).ThenByDescending(e => e.Id).ToList();
        if (offered.Count > 0)
        {
            var newest = offered[0];
            return new(SeekerStatusKinds.Reply, offered.Count, newest.ListingName, newest.Id, Key(SeekerStatusKinds.Reply, offered.Count, newest));
        }
        var confirmed = stayEnquiries.Where(e => e.Status == TripEnquiryStatuses.ConfirmedByBoth)
            .OrderByDescending(e => e.UpdatedAt).ThenByDescending(e => e.Id).ToList();
        if (confirmed.Count == 0) return null;
        var last = confirmed[0];
        return new(SeekerStatusKinds.Confirmed, confirmed.Count, last.ListingName, last.Id, Key(SeekerStatusKinds.Confirmed, confirmed.Count, last));
    }

    public static string Key(string kind, int count, SeekerEnquiryFact newest)
        => $"{kind}:{count}:{newest.Id:N}:{DateTime.SpecifyKind(newest.UpdatedAt, DateTimeKind.Utc).Ticks}";

    // The trip the card shows: the soonest not-ended trip (owned or joined), start date nulls last, then end date, then id.
    public static T? Choose<T>(IEnumerable<T> trips, Func<T, DateOnly?> start, Func<T, DateOnly?> end, Func<T, Guid> id, DateOnly todayIst)
        where T : class
        => trips.Where(t => end(t) is null || end(t) >= todayIst)
            .OrderBy(t => start(t) is null ? 1 : 0).ThenBy(t => start(t)).ThenBy(t => end(t)).ThenBy(id)
            .FirstOrDefault();
}

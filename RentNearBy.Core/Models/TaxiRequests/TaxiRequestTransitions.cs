namespace RentNearBy.Core.Models.TaxiRequests;

public static class TransitionKinds
{
    public const string Request = "Request";
    public const string Offer = "Offer";
    public const string Booking = "Booking";

    public static readonly IReadOnlyList<string> All = [Request, Offer, Booking];
}

// MASTER_PLAN 2.3: the one table of allowed moves. Every store update is "WHERE Status = from" and is checked here first.
public static class TaxiRequestTransitions
{
    private static readonly Dictionary<string, string[]> RequestEdges = new()
    {
        [RequestStates.Open] = [RequestStates.Collecting, RequestStates.Cancelled, RequestStates.Expired],
        [RequestStates.Collecting] = [RequestStates.Selected, RequestStates.Open, RequestStates.Cancelled, RequestStates.Expired],
        [RequestStates.Selected] = [RequestStates.Booked, RequestStates.Collecting, RequestStates.Open, RequestStates.Cancelled, RequestStates.Expired],
        [RequestStates.Booked] = [RequestStates.InProgress, RequestStates.Cancelled, RequestStates.Open, RequestStates.Completed],
        [RequestStates.InProgress] = [RequestStates.Completed, RequestStates.Cancelled],
    };

    private static readonly Dictionary<string, string[]> OfferEdges = new()
    {
        [OfferStates.Ringing] = [OfferStates.Accepted, OfferStates.Declined, OfferStates.Missed, OfferStates.Closed],
        [OfferStates.Accepted] = [OfferStates.Selected, OfferStates.NotAgreed, OfferStates.NotSelected, OfferStates.Withdrawn, OfferStates.Closed],
        [OfferStates.Selected] = [OfferStates.Confirmed, OfferStates.Declined, OfferStates.Missed, OfferStates.NotSelected, OfferStates.Closed],
        [OfferStates.Confirmed] = [OfferStates.Withdrawn],
    };

    private static readonly Dictionary<string, string[]> BookingEdges = new()
    {
        [BookingStates.Booked] = [BookingStates.InProgress, BookingStates.Completed, BookingStates.Cancelled],
        [BookingStates.InProgress] = [BookingStates.Completed, BookingStates.Cancelled],
    };

    public static bool CanMove(string kind, string from, string to)
        => EdgesFor(kind).TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlyList<string> Targets(string kind, string from)
        => EdgesFor(kind).TryGetValue(from, out var targets) ? targets : [];

    public static IReadOnlyList<string> States(string kind) => kind switch
    {
        TransitionKinds.Request => RequestStates.All,
        TransitionKinds.Offer => OfferStates.All,
        TransitionKinds.Booking => BookingStates.All,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown transition kind"),
    };

    private static Dictionary<string, string[]> EdgesFor(string kind) => kind switch
    {
        TransitionKinds.Request => RequestEdges,
        TransitionKinds.Offer => OfferEdges,
        TransitionKinds.Booking => BookingEdges,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown transition kind"),
    };
}

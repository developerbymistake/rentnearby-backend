namespace RentNearBy.Core.Models.TaxiRequests;

// RequestView rule of the contract (section 2): the one place that turns a request into the traveller's screen state.
public static class TaxiRequestViewRules
{
    // waitUntil set and nothing ringing = every remaining driver is busy with another ring (FindingWaiting wins over NoReply).
    public static string Derive(string state, string? endReason, bool anyOfferMissedOrDeclined, bool hasRingingOffer, DateTime? waitUntil, bool rated)
        => state switch
        {
            RequestStates.Open when !hasRingingOffer && waitUntil is not null => RequestViews.FindingWaiting,
            RequestStates.Open when anyOfferMissedOrDeclined => RequestViews.NoReply,
            RequestStates.Open => RequestViews.Finding,
            RequestStates.Collecting => RequestViews.Collecting,
            RequestStates.Selected => RequestViews.Selected,
            RequestStates.Booked => RequestViews.Booked,
            RequestStates.InProgress => RequestViews.InProgress,
            RequestStates.Completed => rated ? RequestViews.Done : RequestViews.Rate,
            RequestStates.Expired when endReason == RequestEndReasons.AllDriversBusy => RequestViews.Busy,
            RequestStates.Expired or RequestStates.Cancelled => RequestViews.Ended,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown request state"),
        };

    // Trip overview Taxi card (contract section 4): chip and button from the view; null or unknown = Optional / Request taxi.
    public static (string Chip, string Button) OverviewCard(string? view) => view switch
    {
        RequestViews.Finding or RequestViews.FindingWaiting or RequestViews.NoReply or RequestViews.Collecting or RequestViews.Selected => ("Requested", "View request"),
        RequestViews.Booked or RequestViews.InProgress => ("Booked", "View booking"),
        RequestViews.Rate or RequestViews.Done => ("Completed", "View booking"),
        _ => ("Optional", "Request taxi"),
    };

    // Make it easy row `taxi` (contract section 4); null state = no request.
    public static string EasyRowState(string? requestState) => requestState switch
    {
        RequestStates.Open => "asked",
        RequestStates.Collecting => "offer",
        RequestStates.Selected => "booked",
        RequestStates.Booked or RequestStates.InProgress or RequestStates.Completed => "confirmed",
        _ => "todo",
    };
}

using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// What a saved journey leg shows for its current lead; shared by the refresh and by every lead state change.
public readonly record struct JourneyLegState(string Status, int? QuotedFare, DateTime KnownAt)
{
    public static JourneyLegState Of(TripEnquiry e)
    {
        // A lapsed reply still names the driver's fare: the traveller can call him on the travel day.
        if (e.Status == TripEnquiryStatuses.Expired && e.DriverFare is { } lapsedFare && e.DriverRepliedAt is { } repliedAt)
            return new(JourneyLegStatuses.Free, lapsedFare, repliedAt);

        var status = JourneyRules.LegStatusOf(e);
        return new(status, JourneyRules.QuotedFareOf(e),
            status == JourneyLegStatuses.NoAnswer ? e.CreatedAt : e.DriverRepliedAt ?? e.UpdatedAt);
    }

    public void ApplyTo(SavedJourneyLeg leg)
    {
        leg.LastKnownStatus = Status;
        leg.LastQuotedFare = QuotedFare;
        leg.LastKnownAt = KnownAt;
    }
}

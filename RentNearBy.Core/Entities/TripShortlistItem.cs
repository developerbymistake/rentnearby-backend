namespace RentNearBy.Core.Entities;

// One listing the traveller shortlisted for one step of a trip (server-owned; the plan JSON never carries it).
// ListingId has no FK: it is a stay id, a local taxi id or a journey car's driver taxi id.
public class TripShortlistItem
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public Guid TravellerUserId { get; set; }
    public string StepKey { get; set; } = string.Empty;
    public string Kind { get; set; } = TripShortlistKinds.Stay;
    public Guid ListingId { get; set; }
    public string ListingName { get; set; } = string.Empty;
    public Guid? JourneyId { get; set; }
    public short? LegIndex { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime ClosesAt { get; set; }
    public string State { get; set; } = TripShortlistStates.Active;
    public string? ClosedReason { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateOnly? RemindedOn { get; set; }
    public DateTime? FinalPushedAt { get; set; }
}

public static class TripShortlistKinds
{
    public const string Stay = "Stay";
    public const string Taxi = "Taxi";
    public const string Car = "Car";

    public static readonly string[] All = [Stay, Taxi, Car];
}

public static class TripShortlistStates
{
    public const string Active = "Active";
    public const string Sent = "Sent";
    public const string Closed = "Closed";
    public const string Booked = "Booked";

    public static readonly string[] All = [Active, Sent, Closed, Booked];
}

public static class TripShortlistClosedReasons
{
    public const string Sent = "Sent";
    public const string Own = "Own";
    public const string Skipped = "Skipped";
    public const string NoReply = "NoReply";
    public const string Removed = "Removed";
    public const string Booked = "Booked";
    public const string TripClosed = "TripClosed";
    // The listing is deleted, off, blocked or the traveller's own (set after a send); ClosedReason is 12 characters at most.
    public const string Unavailable = "Unavailable";
    // The leg or city the row belongs to was removed from the plan.
    public const string LegRemoved = "LegRemoved";
    // The enquiry made from this row was declined, cancelled or answered not available.
    public const string Declined = "Declined";

    // Reasons a client may send to POST .../shortlist/close.
    public static readonly string[] ClientClose = [Own, Skipped];
}

namespace RentNearBy.Core.DTOs.Requests;

public class RespondScheduleRequest
{
    // "accept" | "decline" | "counter"
    public string Action { get; set; } = string.Empty;

    // Required when Action == "counter" — the new set of proposed slots
    // (a proposer can offer more than one time so the other party has a choice).
    public List<DateTime>? ProposedAts { get; set; }

    // Required when Action == "accept" — which of the originally offered slots
    // was picked. Validated against the original proposal's own list server-side.
    public DateTime? AcceptedAt { get; set; }
}

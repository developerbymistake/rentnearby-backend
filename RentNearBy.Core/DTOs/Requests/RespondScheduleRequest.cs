namespace RentNearBy.Core.DTOs.Requests;

public class RespondScheduleRequest
{
    // "accept" | "decline" | "counter"
    public string Action { get; set; } = string.Empty;

    // Required when Action == "counter" — the new proposed slot.
    public DateTime? ProposedAt { get; set; }
}

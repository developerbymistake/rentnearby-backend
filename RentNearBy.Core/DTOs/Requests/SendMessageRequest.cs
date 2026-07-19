namespace RentNearBy.Core.DTOs.Requests;

public class SendMessageRequest
{
    // "quick_reply" | "contact_request" | "schedule_proposal"
    // (contact_response / schedule_response are produced only via their dedicated respond endpoints,
    // never sent directly by a client, so they're not accepted here.)
    public string Type { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";

    // Set only when this is a quick_reply answer — references the question message
    // it answers, so multiple simultaneously-pending questions can each be paired
    // with their own answer regardless of reply order.
    public Guid? RespondsToMessageId { get; set; }

    // Client-generated once per compose-attempt (a fresh send only — never set alongside
    // RespondsToMessageId, which already has its own dedup). Lets the server recognize a
    // genuinely-concurrent double-invocation of the same attempt instead of creating a
    // real duplicate message.
    public Guid? ClientMessageId { get; set; }
}

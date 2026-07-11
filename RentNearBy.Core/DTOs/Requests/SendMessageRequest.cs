namespace RentNearBy.Core.DTOs.Requests;

public class SendMessageRequest
{
    // "quick_reply" | "contact_request" | "schedule_proposal"
    // (contact_response / schedule_response are produced only via their dedicated respond endpoints,
    // never sent directly by a client, so they're not accepted here.)
    public string Type { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
}

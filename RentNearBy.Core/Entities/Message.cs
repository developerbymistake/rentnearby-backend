namespace RentNearBy.Core.Entities;

public class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Conversation? Conversation { get; set; }
    public Guid SenderId { get; set; }

    // "quick_reply" | "contact_request" | "contact_response" | "schedule_proposal" | "schedule_response" | "system"
    public string Type { get; set; } = string.Empty;

    // Catalog keys only (e.g. {"key":"is_available"} or {"answerKey":"yes_available"}), never full text.
    public string PayloadJson { get; set; } = "{}";

    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

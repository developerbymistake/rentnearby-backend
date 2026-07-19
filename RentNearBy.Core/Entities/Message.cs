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

    // Which message this one answers — currently only used by quick_reply answers, so a
    // question can be paired with its own answer regardless of how many other questions
    // are pending at the same time. A real column (not another PayloadJson field) so it's
    // queryable and DB-enforced to at most one answer per question (see the partial unique
    // index on this column).
    public Guid? RespondsToMessageId { get; set; }

    // Client-generated once per compose-attempt (never per retry of the same attempt) —
    // only set on a fresh send (RespondsToMessageId null), never on an answer, which already
    // has its own dedup via RespondsToMessageId's unique index. Lets a genuinely-concurrent
    // double-invocation (e.g. a fast double-tap landing before a sheet visually dismisses)
    // be recognized as the same attempt server-side instead of creating a real duplicate.
    public Guid? ClientMessageId { get; set; }

    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

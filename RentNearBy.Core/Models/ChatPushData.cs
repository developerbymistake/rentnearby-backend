namespace RentNearBy.Core.Models;

// FCM data map of a chat push. message_id is the idempotency key and created_at (unix ms) the order key, so the
// app stacks lines by message order and drops a retried or reordered duplicate.
public static class ChatPushData
{
    public const string ConversationId = "conversation_id";
    public const string Title = "title";
    public const string Body = "body";
    public const string MessageId = "message_id";
    public const string CreatedAt = "created_at";

    public static Dictionary<string, string> Build(Guid conversationId, string title, string body, Guid? messageId, DateTime? createdAtUtc)
    {
        var data = new Dictionary<string, string>
        {
            { ConversationId, conversationId.ToString() },
            { Title, title },
            { Body, body },
        };
        if (messageId is { } id) data[MessageId] = id.ToString();
        if (createdAtUtc is { } at)
            data[CreatedAt] = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return data;
    }
}

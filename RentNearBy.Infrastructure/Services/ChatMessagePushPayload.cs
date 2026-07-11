namespace RentNearBy.Infrastructure.Services;

public class ChatMessagePushPayload
{
    public Guid RecipientUserId { get; set; }
    public Guid ConversationId { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;
}

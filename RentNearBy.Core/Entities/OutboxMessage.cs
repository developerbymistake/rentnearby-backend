namespace RentNearBy.Core.Entities;

// Transactional outbox: inserted in the same transaction as its cause, published by the relay with publisher confirms.
// Key is unique, so a producer that runs twice writes the message once.
public class OutboxMessage
{
    public long Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string RoutingKey { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public short Attempts { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? LastError { get; set; }
}

public static class OutboxTargets
{
    public const string PushExchange = "rnb.push";
    public const string PushRoutingKey = "send";
}

// Payload of a push outbox row (also the FCM data map). NotificationId is set when an inbox row exists.
public sealed record OutboxPushPayload(
    string Key, Guid UserId, Guid? NotificationId, string Title, string Body, string? Route, string? ArgsJson, string Tag, int Attempt = 0);

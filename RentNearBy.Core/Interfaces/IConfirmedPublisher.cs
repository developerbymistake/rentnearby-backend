namespace RentNearBy.Core.Interfaces;

public sealed record ConfirmedMessage(string Exchange, string RoutingKey, string Body, string MessageId, int Attempt = 0);

public readonly record struct PublishResult(bool Confirmed, string? Error = null);

// Publishes with broker confirms and mandatory routing. One result per message, same order; a failure never throws for the batch.
public interface IConfirmedPublisher
{
    Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<ConfirmedMessage> messages, CancellationToken ct = default);
}

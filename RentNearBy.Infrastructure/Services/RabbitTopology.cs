using RabbitMQ.Client;

namespace RentNearBy.Infrastructure.Services;

// Additive push topology (the old notification.push queue keeps its null args and its own worker).
//   rnb.push (direct) --send--> push.send (dead-letters to rnb.push.dlx)
//                     --retry.30s--> push.retry.30s (TTL 30 s, back to rnb.push/send)
//                     --retry.5m--> push.retry.5m  (TTL 5 min, back to rnb.push/send)
//                     --dead--> push.dead (no consumer; alert on depth)
// Declared once per connection by the publisher and by the consumer, never per publish.
public static class RabbitTopology
{
    public const string Exchange = "rnb.push", DeadExchange = "rnb.push.dlx";
    public const string SendKey = "send", Retry30Key = "retry.30s", Retry5mKey = "retry.5m", DeadKey = "dead";
    public const string SendQueue = "push.send", Retry30Queue = "push.retry.30s", Retry5mQueue = "push.retry.5m", DeadQueue = "push.dead";
    public const string AttemptHeader = "x-attempt";
    public const int Retry30Ms = 30_000, Retry5mMs = 300_000;

    // failureCount 1 -> 30 s, 2 -> 5 min, 3 or more -> push.dead.
    public static string NextRoute(int failureCount) => failureCount switch
    {
        <= 1 => Retry30Key,
        2 => Retry5mKey,
        _ => DeadKey,
    };

    public static async Task DeclareAsync(IChannel channel, CancellationToken ct = default)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);

        await channel.QueueDeclareAsync(SendQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = DeadExchange }, cancellationToken: ct);
        await channel.QueueBindAsync(SendQueue, Exchange, SendKey, cancellationToken: ct);

        await DeclareRetryAsync(channel, Retry30Queue, Retry30Key, Retry30Ms, ct);
        await DeclareRetryAsync(channel, Retry5mQueue, Retry5mKey, Retry5mMs, ct);

        await channel.QueueDeclareAsync(DeadQueue, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: ct);
        await channel.QueueBindAsync(DeadQueue, Exchange, DeadKey, cancellationToken: ct);
        await channel.QueueBindAsync(DeadQueue, DeadExchange, SendKey, cancellationToken: ct);
    }

    private static async Task DeclareRetryAsync(IChannel channel, string queue, string key, int ttlMs, CancellationToken ct)
    {
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = ttlMs,
                ["x-dead-letter-exchange"] = Exchange,
                ["x-dead-letter-routing-key"] = SendKey,
            }, cancellationToken: ct);
        await channel.QueueBindAsync(queue, Exchange, key, cancellationToken: ct);
    }
}

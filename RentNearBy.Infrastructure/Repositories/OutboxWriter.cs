using System.Text.Json;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public sealed class OutboxWriter(ApplicationDbContext context, IOutboxSignal? signal = null) : IOutboxWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Add(string key, string exchange, string routingKey, string payload, DateTime nowUtc)
        => context.OutboxMessages.Add(new OutboxMessage
        {
            Key = key,
            Exchange = exchange,
            RoutingKey = routingKey,
            Payload = payload,
            CreatedAt = nowUtc,
            NextAttemptAt = nowUtc,
        });

    public void AddPush(OutboxPushPayload push, DateTime nowUtc)
        => Add(push.Key, OutboxTargets.PushExchange, OutboxTargets.PushRoutingKey, JsonSerializer.Serialize(push, Json), nowUtc);

    public void Wake() => signal?.Wake();
}

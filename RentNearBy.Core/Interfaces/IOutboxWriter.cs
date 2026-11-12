using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

// Adds outbox rows to the current DbContext unit of work; the caller's SaveChanges (inside its transaction) persists them.
public interface IOutboxWriter
{
    void Add(string key, string exchange, string routingKey, string payload, DateTime nowUtc);

    // Push for rnb.push: payload built from the record, routing key send.
    void AddPush(OutboxPushPayload push, DateTime nowUtc);

    // After the transaction committed: wakes the relay (no-op when none is registered).
    void Wake();
}

// Registered by the relay (B3); the writer calls it after commit.
public interface IOutboxSignal
{
    void Wake();
}

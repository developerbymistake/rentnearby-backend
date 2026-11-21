using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Core.Interfaces.Owner;

// One push of this work. Text lives only in the transient outbox row; never an amount (contract 5.3).
public sealed record OwnerPush(
    Guid UserId,
    string Type,
    string Key,
    string Title,
    string Body,
    string Route,
    string ArgsJson,
    string Tag,
    DateTime? ExpiresAtUtc = null);

// Every push and stored notice of the owner roles goes through here: UserNotices rows and rnb.push outbox rows on the caller's
// context and transaction, relayed to RabbitMQ by OutboxRelayService. Never FCM, never NotificationEvents (contract 5.4, guard 11.5).
public interface IOwnerNotifier
{
    // One statement each, on the caller's context and transaction, whatever the number of users.
    Task StoreAsync(IReadOnlyCollection<NoticeUpsert> notices, CancellationToken ct = default);

    Task ClearAsync(string kind, Guid entityId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    // A row is written only for a user with a valid device token (EXISTS in the same statement); duplicate keys are dropped.
    Task PushAsync(IReadOnlyCollection<OwnerPush> pushes, CancellationToken ct = default);

    // After the caller's commit: wakes the outbox relay.
    void Wake();
}

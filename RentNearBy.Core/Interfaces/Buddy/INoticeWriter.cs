using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Core.Interfaces.Buddy;

// Works on the caller's context and transaction. Every method is ONE statement whatever the number of recipients.
public interface INoticeWriter
{
    Task UpsertAsync(IReadOnlyCollection<NoticeUpsert> notices, CancellationToken ct = default);

    // false = a newer row exists, nothing changed.
    Task<bool> UpsertIfOlderAsync(NoticeUpsert notice, DateTime notBeforeUtc, CancellationToken ct = default);

    // userIds null = every user.
    Task DeleteAsync(string kind, Guid tripId, IReadOnlyCollection<Guid>? userIds, CancellationToken ct = default);

    Task DeleteForTripAsync(Guid tripId, IReadOnlyCollection<string> exceptKinds, CancellationToken ct = default);

    Task DeletePairsAsync(Guid tripId, IReadOnlyCollection<(string Kind, Guid UserId)> rows, CancellationToken ct = default);

    Task PushAsync(IReadOnlyCollection<PushSpec> pushes, CancellationToken ct = default);

    // After the caller's commit: wakes the outbox relay.
    void Wake();
}

using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Tests.Support.Buddy;

// Records every call for unit-level service tests (BE1 to BE3). Calls = how many statements the real writer would send.
public sealed class RecordingNoticeWriter : INoticeWriter
{
    public List<NoticeUpsert> Upserts { get; } = [];
    public List<(NoticeUpsert Notice, DateTime NotBeforeUtc)> ConditionalUpserts { get; } = [];
    public List<(string Kind, Guid TripId, IReadOnlyCollection<Guid>? UserIds)> Deletes { get; } = [];
    public List<(Guid TripId, IReadOnlyCollection<string> ExceptKinds)> TripDeletes { get; } = [];
    public List<PushSpec> Pushes { get; } = [];
    public int Calls { get; private set; }
    public int Wakes { get; private set; }
    public bool UpsertIfOlderResult { get; set; } = true;

    public Task UpsertAsync(IReadOnlyCollection<NoticeUpsert> notices, CancellationToken ct = default)
    {
        Calls++;
        Upserts.AddRange(notices);
        return Task.CompletedTask;
    }

    public Task<bool> UpsertIfOlderAsync(NoticeUpsert notice, DateTime notBeforeUtc, CancellationToken ct = default)
    {
        Calls++;
        ConditionalUpserts.Add((notice, notBeforeUtc));
        return Task.FromResult(UpsertIfOlderResult);
    }

    public Task DeleteAsync(string kind, Guid tripId, IReadOnlyCollection<Guid>? userIds, CancellationToken ct = default)
    {
        Calls++;
        Deletes.Add((kind, tripId, userIds?.ToList()));
        return Task.CompletedTask;
    }

    public Task DeleteForTripAsync(Guid tripId, IReadOnlyCollection<string> exceptKinds, CancellationToken ct = default)
    {
        Calls++;
        TripDeletes.Add((tripId, exceptKinds.ToList()));
        return Task.CompletedTask;
    }

    public Task PushAsync(IReadOnlyCollection<PushSpec> pushes, CancellationToken ct = default)
    {
        Calls++;
        Pushes.AddRange(pushes);
        return Task.CompletedTask;
    }

    public void Wake() => Wakes++;
}

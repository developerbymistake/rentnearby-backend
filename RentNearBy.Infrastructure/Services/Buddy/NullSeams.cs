using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Infrastructure.Services.Buddy;

// Safe no-op defaults (registered with TryAdd); each lane replaces its own with services.Replace.

public sealed class NullNoticeWriter : INoticeWriter
{
    public Task UpsertAsync(IReadOnlyCollection<NoticeUpsert> notices, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> UpsertIfOlderAsync(NoticeUpsert notice, DateTime notBeforeUtc, CancellationToken ct = default) => Task.FromResult(true);

    public Task DeleteAsync(string kind, Guid tripId, IReadOnlyCollection<Guid>? userIds, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteForTripAsync(Guid tripId, IReadOnlyCollection<string> exceptKinds, CancellationToken ct = default) => Task.CompletedTask;

    public Task PushAsync(IReadOnlyCollection<PushSpec> pushes, CancellationToken ct = default) => Task.CompletedTask;

    public void Wake()
    {
    }
}

public sealed class NullTripMoneyCards : ITripMoneyCards
{
    public Task<IReadOnlyDictionary<Guid, TripMoneyCard>> GetCardsAsync(IReadOnlyCollection<Guid> tripIds, Guid userId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, TripMoneyCard>>(new Dictionary<Guid, TripMoneyCard>());

    public Task<IReadOnlyDictionary<Guid, TripMoneyStanding>> GetStandingsAsync(IReadOnlyCollection<Guid> tripIds, Guid userId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, TripMoneyStanding>>(new Dictionary<Guid, TripMoneyStanding>());
}

public sealed class NullTripMoneyGuard : ITripMoneyGuard
{
    public Task<IReadOnlyList<OpenPaymentLine>> GetOpenPaymentsAsync(Guid tripId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OpenPaymentLine>>([]);
}

public sealed class NullTripSharingHooks : ITripSharingHooks
{
    public Task<TripSharingBlockDto?> GetSharingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default)
        => Task.FromResult<TripSharingBlockDto?>(null);

    public Task OnPlanSavedAsync(Guid tripId, Guid ownerId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<int> OnTripDeletingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default) => Task.FromResult(0);
}

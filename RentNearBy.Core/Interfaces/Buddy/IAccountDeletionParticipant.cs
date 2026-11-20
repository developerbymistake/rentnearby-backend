using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces.Buddy;

public interface IAccountDeletionParticipant
{
    int Order { get; }

    // Any block -> 409 AccountHasActiveItems.
    Task<IReadOnlyList<AccountDeletionBlock>> CheckAsync(Guid userId, CancellationToken ct = default);

    // Inside the deletion transaction, before the Users row goes.
    Task OnDeletingAsync(Guid userId, CancellationToken ct = default);

    Task AfterCommitAsync(Guid userId, CancellationToken ct = default);
}

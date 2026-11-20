namespace RentNearBy.Core.Interfaces.Buddy;

public interface IUserBlockParticipant
{
    // Inside the block transaction, after the pair lock.
    Task OnBlockedAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default);
}

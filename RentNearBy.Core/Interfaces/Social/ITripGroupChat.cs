namespace RentNearBy.Core.Interfaces.Social;

// Trip group chat (contract 3.5). Both run inside the caller's transaction; their hub events are queued on the scoped
// SocialAfterCommit collector and sent when the caller flushes it after commit.
public interface ITripGroupChat
{
    // Room id of the trip (created at the first join).
    Task<Guid> OnMemberJoinedAsync(Guid tripId, Guid userId, CancellationToken ct = default);

    Task OnMemberGoneAsync(Guid tripId, Guid userId, CancellationToken ct = default);
}

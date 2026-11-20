namespace RentNearBy.Core.Interfaces.Buddy;

// Done: the end state holds (also on a repeat). TargetMissing: unknown person, nothing written. TooManyBlocks: the 1,000 cap.
public enum BlockResult { Done, TargetMissing, TooManyBlocks }

// FlippedConversationIds: conversations whose status changed (for the chat hub push).
public sealed record BlockOutcome(BlockResult Result, IReadOnlyList<Guid> FlippedConversationIds)
{
    public static readonly BlockOutcome Missing = new(BlockResult.TargetMissing, []);
    public static readonly BlockOutcome Full = new(BlockResult.TooManyBlocks, []);
}

// The one block of the app: chat, buddies and trip invites (contract 3.3).
public interface IUserBlockService
{
    Task<BlockOutcome> BlockAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default);

    Task<BlockOutcome> UnblockAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default);
}

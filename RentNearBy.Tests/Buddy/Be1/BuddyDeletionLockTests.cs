using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services.Buddy.Be1;

namespace RentNearBy.Tests.Buddy.Be1;

// P10b: account deletion takes the buddy pair locks before the Users row lock (contract 5.1 order).
public class BuddyDeletionLockTests
{
    [Fact]
    public void Partners_are_buddies_and_open_requests_both_ways()
    {
        var sql = BuddyAccountDeletion.PartnersSql;
        Assert.Contains("FROM \"Buddies\" b WHERE b.\"UserId\" = @me", sql);
        Assert.Contains("r.\"FromUserId\" = @me AND r.\"Status\" = 'Pending'", sql);
        Assert.Contains("r.\"ToUserId\" = @me AND r.\"Status\" = 'Pending'", sql);
        Assert.DoesNotContain("FOR UPDATE", sql);
    }

    [Fact]
    public async Task Participants_without_locks_keep_the_default_no_op()
    {
        IAccountDeletionParticipant p = new NoLocks();
        await p.LockAsync(Guid.NewGuid());
    }

    private sealed class NoLocks : IAccountDeletionParticipant
    {
        public int Order => 99;
        public Task<IReadOnlyList<AccountDeletionBlock>> CheckAsync(Guid userId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AccountDeletionBlock>>([]);
        public Task OnDeletingAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task AfterCommitAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
    }
}

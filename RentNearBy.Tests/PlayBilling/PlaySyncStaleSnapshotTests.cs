using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.PlayBilling;

// Matrix L15 out-of-order notifications: a snapshot fetched before another sync wrote the row is never applied over it (the sync
// reads Google again under the retry loop). Pure.
public class PlaySyncStaleSnapshotTests
{
    private static readonly DateTime Fetched = new(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_row_synced_after_the_fetch_makes_the_snapshot_stale()
        => Assert.True(PlayBillingSyncService.IsStale(Fetched.AddMilliseconds(5), Fetched));

    [Fact]
    public void A_row_synced_before_the_fetch_or_never_synced_is_applied()
    {
        Assert.False(PlayBillingSyncService.IsStale(Fetched.AddSeconds(-1), Fetched));
        Assert.False(PlayBillingSyncService.IsStale(Fetched, Fetched));
        Assert.False(PlayBillingSyncService.IsStale(null, Fetched));
    }

    [Fact]
    public void The_sync_checks_staleness_under_the_token_lock_and_fetches_again()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "RentNearBy.Infrastructure", "Services", "PlayBillingSyncService.cs"));
        var locked = src.IndexOf("await subscriptions.LockByTokenAsync(token);", StringComparison.Ordinal);
        var check = src.IndexOf("IsStale(sub.LastSyncedAt, fetchedAt)", StringComparison.Ordinal);
        Assert.True(locked > 0 && check > locked);
        Assert.Contains("fetched = await FetchAsync(request);", src);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found");
    }
}

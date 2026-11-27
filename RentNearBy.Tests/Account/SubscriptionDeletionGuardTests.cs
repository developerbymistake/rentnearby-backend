using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services.Owner.Quote;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Account;

// Q20 / ARCHITECTURE 11 (all roles): an AUTO-RENEWING Play subscription blocks account deletion with one clear step
// (ManagePlay + manageUrl). Time-bounded: a stuck row never blocks. Token gone at Google never blocks. Sync failure keeps the DB
// state and adds the "just cancelled?" line. Cancelled-in-period and ended subscriptions do not block.
public class SubscriptionDeletionGuardTests
{
    internal static readonly DateTime Now = DateTime.UtcNow;

    internal static PlaySubscription Row(Guid user, string state, bool autoRenew, DateTime? expiry, DateTime? pausedResume = null,
        string mapping = PlayMappingStatuses.Mapped) => new()
    {
        Id = Guid.NewGuid(), UserId = user, State = state, AutoRenewEnabled = autoRenew, ExpiryTime = expiry, PausedAutoResumeAt = pausedResume,
        MappingStatus = mapping, PurchaseToken = "tok-" + Guid.NewGuid().ToString("N"), ProductId = "stay_plus", PurchasedProductId = "stay_plus",
        AcknowledgementState = PlayAckStates.Acknowledged, CreatedAt = Now, UpdatedAt = Now,
    };

    public static TheoryData<string, bool, int?, int?, string, bool> Cases => new()
    {
        // state, autoRenew, expiry (days from now), paused resume (days), mapping, blocks
        { PlayStates.Active, true, 20, null, PlayMappingStatuses.Mapped, true },
        { PlayStates.InGracePeriod, true, 2, null, PlayMappingStatuses.Mapped, true },
        { PlayStates.OnHold, true, -10, null, PlayMappingStatuses.Mapped, true },
        { PlayStates.OnHold, true, -61, null, PlayMappingStatuses.Mapped, false },
        { PlayStates.Paused, true, -5, 10, PlayMappingStatuses.Mapped, true },
        { PlayStates.Paused, true, -5, -1, PlayMappingStatuses.Mapped, false },
        { PlayStates.Active, false, 20, null, PlayMappingStatuses.Mapped, false },
        { PlayStates.Canceled, false, 20, null, PlayMappingStatuses.Mapped, false },
        { PlayStates.Expired, false, -3, null, PlayMappingStatuses.Mapped, false },
        { PlayStates.Active, true, -2, null, PlayMappingStatuses.Mapped, false },
        { PlayStates.Active, true, 20, null, PlayMappingStatuses.Detached, false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Time_bounded_predicate(string state, bool autoRenew, int? expiryDays, int? resumeDays, string mapping, bool blocks)
    {
        var user = Guid.NewGuid();
        var row = Row(user, state, autoRenew, expiryDays is { } d ? Now.AddDays(d) : null, resumeDays is { } r ? Now.AddDays(r) : null, mapping);
        var predicate = SubscriptionDeletionGuard.Blocking(user, Now).Compile();
        Assert.Equal(blocks, predicate(row));
        Assert.False(SubscriptionDeletionGuard.Blocking(Guid.NewGuid(), Now).Compile()(row));
    }

    [Fact]
    public void Block_carries_code_action_manage_url_and_title()
    {
        var block = SubscriptionDeletionGuard.Block("https://play.google.com/store/account/subscriptions?sku=stay_plus&package=com.x", syncUnsure: false);
        Assert.Equal(AccountDeletionReasons.ActiveSubscription, block.Code);
        Assert.Equal(AccountDeletionActions.ManagePlay, block.Action);
        Assert.Equal(AccountDeletionReasons.ActiveSubscriptionMessage, block.Message);
        Assert.Equal(AccountDeletionReasons.ActiveSubscriptionTitle, block.Title);
        Assert.Equal("https://play.google.com/store/account/subscriptions?sku=stay_plus&package=com.x", block.Args!.Value.GetProperty("manageUrl").GetString());
        Assert.EndsWith(AccountDeletionReasons.JustCancelledHint, SubscriptionDeletionGuard.Block("u", syncUnsure: true).Message);
    }

    [Fact]
    public void Manage_url_is_the_same_builder_as_my_plan()
    {
        Assert.Equal("https://play.google.com/store/account/subscriptions?sku=stay_plus", PlayManageUrls.For("stay_plus", null));
        Assert.Equal("https://play.google.com/store/account/subscriptions?sku=stay_plus&package=com.rentnearby", PlayManageUrls.For("stay_plus", "com.rentnearby"));
    }

    [Theory]
    [InlineData(PlaySyncOutcome.Invalid, null, true)]
    [InlineData(PlaySyncOutcome.ApiError, PlayApiErrorKind.NotFound, true)]
    [InlineData(PlaySyncOutcome.ApiError, PlayApiErrorKind.Invalid, true)]
    [InlineData(PlaySyncOutcome.ApiError, PlayApiErrorKind.Transient, false)]
    [InlineData(PlaySyncOutcome.Cancelled, null, false)]
    public void Token_gone_outcomes(PlaySyncOutcome outcome, PlayApiErrorKind? error, bool gone)
        => Assert.Equal(gone, SubscriptionDeletionPreCheck.IsGone(new PlaySyncResult(outcome, null, null, null, null, null, null, error, null, null)));

    [Fact]
    public void Context_travels_to_the_retry_scope()
    {
        var first = new SubscriptionDeletionContext { SyncUnsure = true };
        var id = Guid.NewGuid();
        first.GoneSubscriptionIds.Add(id);
        var retry = new SubscriptionDeletionContext();
        retry.CopyFrom(first);
        Assert.True(retry.SyncUnsure);
        Assert.Contains(id, retry.GoneSubscriptionIds);
    }

    [Fact]
    public void Block_serialises_args_as_an_object()
    {
        var json = JsonSerializer.Serialize(SubscriptionDeletionGuard.Block("u", false), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"args\":{\"manageUrl\":\"u\"}", json);
        Assert.DoesNotContain("argsJson", json);
    }
}

public class SubscriptionDeletionPreCheckScopeTests
{
    // A context that never opens a connection: enough to tell instances apart and to see a disposed one.
    private static RentNearBy.Infrastructure.Data.ApplicationDbContext DesignContext() => new(
        new DbContextOptionsBuilder<RentNearBy.Infrastructure.Data.ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite()).Options);

    internal sealed class ScopeProbe : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    internal sealed class SlowRecord
    {
        public RentNearBy.Infrastructure.Data.ApplicationDbContext? SeenContext;
        public bool ProbeDisposedWhenTouched;
        public readonly TaskCompletionSource Touched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Waits for the test to release it, then touches its own scope's context (as the real sync does after a slow Google call).
    internal sealed class SlowSync(RentNearBy.Infrastructure.Data.ApplicationDbContext own, ScopeProbe probe, TaskCompletionSource release, SlowRecord record)
        : IPlayBillingSyncService
    {
        public async Task<PlaySyncResult> SyncTokenAsync(PlaySyncRequest request)
        {
            await release.Task;
            record.SeenContext = own;
            _ = own.ChangeTracker.Entries().Count(); // throws ObjectDisposedException on a disposed context
            record.ProbeDisposedWhenTouched = probe.Disposed;
            record.Touched.TrySetResult();
            return new PlaySyncResult(PlaySyncOutcome.Unchanged, null, null, null, null, null, null, null, null, null);
        }

        public Task<PlayRtdnOutcome> ProcessRtdnAsync(string messageId, string dataJson, DateTime publishTime) => throw new NotSupportedException();
        public Task<PlaySyncResult> ApplyVoidedAsync(string purchaseToken, string? orderId, int refundType, string source) => throw new NotSupportedException();
        public Task<int> RunPendingServerActionsAsync(int batchSize) => throw new NotSupportedException();
        public Task<int> SyncDueAsync(int batchSize) => throw new NotSupportedException();
        public Task<int> SyncStaleAsync(int batchSize) => throw new NotSupportedException();
        public Task<int> ReconcileVoidedAsync() => throw new NotSupportedException();
        public Task<AdminPurchaseDto> AdminRefundPlayAsync(Guid adminId, Guid purchaseId, string reason) => throw new NotSupportedException();
    }

    private static SubscriptionDeletionPreCheck PreCheck(RentNearBy.Infrastructure.Data.ApplicationDbContext requestDb, SubscriptionDeletionContext ctx,
        IServiceProvider root)
        => new(requestDb, ctx, root.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<SubscriptionDeletionPreCheck>.Instance);

    // I-1: a sync that outlives the budget runs in its own scope with its own context; the deletion's context is never shared,
    // the answer is the block with the "just cancelled?" line, and the scope is disposed only after the late sync has ended.
    [Fact]
    public async Task A_slow_sync_uses_its_own_scope_and_its_scope_outlives_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var record = new SlowRecord();
        var probes = new List<ScopeProbe>();
        var services = new ServiceCollection();
        services.AddScoped(_ => DesignContext());
        services.AddScoped(_ =>
        {
            var p = new ScopeProbe();
            probes.Add(p);
            return p;
        });
        services.AddScoped<IPlayBillingSyncService>(sp => new SlowSync(
            sp.GetRequiredService<RentNearBy.Infrastructure.Data.ApplicationDbContext>(), sp.GetRequiredService<ScopeProbe>(), release, record));
        await using var root = services.BuildServiceProvider();

        await using var requestDb = DesignContext();
        var ctx = new SubscriptionDeletionContext();
        var precheck = new SubscriptionDeletionPreCheck(requestDb, ctx, root.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<SubscriptionDeletionPreCheck>.Instance) { Budget = TimeSpan.FromMilliseconds(100) };

        await precheck.SyncTokensAsync([(Guid.NewGuid(), "tok-a"), (Guid.NewGuid(), "tok-b")], ct);

        // Budget spent on the first token: the second is never started, the answer is "unsure".
        Assert.True(ctx.SyncUnsure);
        Assert.Empty(ctx.GoneSubscriptionIds);
        var probe = Assert.Single(probes);
        Assert.False(probe.Disposed);
        Assert.EndsWith(AccountDeletionReasons.JustCancelledHint, SubscriptionDeletionGuard.Block("u", ctx.SyncUnsure).Message);

        // The deletion's own context is free for its transaction while the late sync is still running.
        Assert.Empty(requestDb.ChangeTracker.Entries());

        release.SetResult();
        await record.Touched.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.False(record.ProbeDisposedWhenTouched);
        Assert.NotNull(record.SeenContext);
        Assert.NotSame(requestDb, record.SeenContext);

        var until = DateTime.UtcNow.AddSeconds(10);
        while (!probe.Disposed && DateTime.UtcNow < until) await Task.Delay(10, ct);
        Assert.True(probe.Disposed);
    }

    private sealed class AnswerSync(Func<PlaySyncResult> answer) : IPlayBillingSyncService
    {
        public int Calls;
        public Task<PlaySyncResult> SyncTokenAsync(PlaySyncRequest request) { Calls++; return Task.FromResult(answer()); }
        public Task<PlayRtdnOutcome> ProcessRtdnAsync(string messageId, string dataJson, DateTime publishTime) => throw new NotSupportedException();
        public Task<PlaySyncResult> ApplyVoidedAsync(string purchaseToken, string? orderId, int refundType, string source) => throw new NotSupportedException();
        public Task<int> RunPendingServerActionsAsync(int batchSize) => throw new NotSupportedException();
        public Task<int> SyncDueAsync(int batchSize) => throw new NotSupportedException();
        public Task<int> SyncStaleAsync(int batchSize) => throw new NotSupportedException();
        public Task<int> ReconcileVoidedAsync() => throw new NotSupportedException();
        public Task<AdminPurchaseDto> AdminRefundPlayAsync(Guid adminId, Guid purchaseId, string reason) => throw new NotSupportedException();
    }

    private static PlaySyncResult Result(PlaySyncOutcome outcome, PlayApiErrorKind? error = null)
        => new(outcome, null, null, null, null, null, null, error, null, null);

    [Fact]
    public async Task Each_token_gets_a_fresh_scope_and_outcomes_map_to_gone_or_unsure()
    {
        var ct = TestContext.Current.CancellationToken;
        var answers = new Queue<PlaySyncResult>([Result(PlaySyncOutcome.ApiError, PlayApiErrorKind.NotFound), Result(PlaySyncOutcome.Cancelled)]);
        var sync = new AnswerSync(() => answers.Dequeue());
        var scopesMade = 0;
        var services = new ServiceCollection();
        services.AddScoped<IPlayBillingSyncService>(_ =>
        {
            scopesMade++;
            return sync;
        });
        await using var root = services.BuildServiceProvider();
        await using var requestDb = DesignContext();
        var ctx = new SubscriptionDeletionContext();
        var gone = Guid.NewGuid();
        await PreCheck(requestDb, ctx, root).SyncTokensAsync([(gone, "tok-a"), (Guid.NewGuid(), "tok-b")], ct);
        Assert.Equal(2, sync.Calls);
        Assert.Equal(2, scopesMade);
        Assert.Contains(gone, ctx.GoneSubscriptionIds);
        Assert.False(ctx.SyncUnsure);

        var transient = new SubscriptionDeletionContext();
        var other = new ServiceCollection();
        other.AddScoped<IPlayBillingSyncService>(_ => new AnswerSync(() => Result(PlaySyncOutcome.ApiError, PlayApiErrorKind.Transient)));
        await using var root2 = other.BuildServiceProvider();
        await PreCheck(requestDb, transient, root2).SyncTokensAsync([(Guid.NewGuid(), "tok")], ct);
        Assert.True(transient.SyncUnsure);
    }

    [Fact]
    public async Task No_sync_service_means_unsure()
    {
        await using var root = new ServiceCollection().BuildServiceProvider();
        await using var requestDb = DesignContext();
        var ctx = new SubscriptionDeletionContext();
        await PreCheck(requestDb, ctx, root).SyncTokensAsync([(Guid.NewGuid(), "tok")], TestContext.Current.CancellationToken);
        Assert.True(ctx.SyncUnsure);
    }

    [Theory]
    [InlineData(PlaySyncOutcome.ApiError, false)]
    [InlineData(PlaySyncOutcome.Cancelled, true)]
    [InlineData(PlaySyncOutcome.Renewed, true)]
    [InlineData(PlaySyncOutcome.Unchanged, true)]
    public void Only_an_api_error_is_not_a_state_update(PlaySyncOutcome outcome, bool update)
        => Assert.Equal(update, SubscriptionDeletionPreCheck.IsStateUpdate(Result(outcome)));
}

public class SubscriptionDeletionGuardIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly DateTime Now = SubscriptionDeletionGuardTests.Now;

    private static PlaySubscription Row(Guid user, string state, bool autoRenew, DateTime? expiry)
        => SubscriptionDeletionGuardTests.Row(user, state, autoRenew, expiry);

    private sealed class FakeSync(Func<PlaySyncRequest, Task<PlaySyncResult>> answer) : IPlayBillingSyncService
    {
        public int Calls;
        public Task<PlaySyncResult> SyncTokenAsync(PlaySyncRequest request) { Calls++; return answer(request); }
        public Task<PlayRtdnOutcome> ProcessRtdnAsync(string messageId, string dataJson, DateTime publishTime) => throw new NotSupportedException();
        public Task<PlaySyncResult> ApplyVoidedAsync(string purchaseToken, string? orderId, int refundType, string source) => throw new NotSupportedException();
        public Task<int> RunPendingServerActionsAsync(int batchSize) => throw new NotSupportedException();
        public Task<int> SyncDueAsync(int batchSize) => throw new NotSupportedException();
        public Task<int> SyncStaleAsync(int batchSize) => throw new NotSupportedException();
        public Task<int> ReconcileVoidedAsync() => throw new NotSupportedException();
        public Task<AdminPurchaseDto> AdminRefundPlayAsync(Guid adminId, Guid purchaseId, string reason) => throw new NotSupportedException();
    }

    // The pre-check resolves the sync from a NEW scope per token (never the request's context).
    private static IServiceScopeFactory Services(IPlayBillingSyncService? sync)
    {
        var services = new ServiceCollection();
        if (sync != null) services.AddScoped(_ => sync);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static SubscriptionDeletionGuard Guard(RentNearBy.Infrastructure.Data.ApplicationDbContext c, SubscriptionDeletionContext ctx)
        => new(c, ctx, Options.Create(new PlayBillingOptions { PackageName = "com.rentnearby" }), TimeProvider.System);

    private async Task<Guid> UserWithAsync(Guid roleId, PlaySubscription? row)
    {
        await using var c = CreateContext();
        var user = await OwnerSeed.UserAsync(c, roleId);
        if (row != null)
        {
            row.UserId = user;
            c.PlaySubscriptions.Add(row);
            await c.SaveChangesAsync();
        }
        return user;
    }

    [IntegrationTheory]
    [InlineData("seeker")]
    [InlineData("stay")]
    [InlineData("taxi")]
    public async Task Auto_renewing_subscription_blocks_every_role(string role)
    {
        var roleId = role switch { "stay" => UserRoles.StayOwnerId, "taxi" => UserRoles.TaxiDriverId, _ => UserRoles.SeekerId };
        var user = await UserWithAsync(roleId, Row(Guid.Empty, PlayStates.Active, true, Now.AddDays(20)));
        await using var c = CreateContext();
        var blocks = await Guard(c, new SubscriptionDeletionContext()).CheckAsync(user);
        var block = Assert.Single(blocks);
        Assert.Equal(AccountDeletionActions.ManagePlay, block.Action);
        Assert.Contains("package=com.rentnearby", block.Args!.Value.GetProperty("manageUrl").GetString());
    }

    [IntegrationFact]
    public async Task Cancelled_in_period_and_stuck_rows_do_not_block()
    {
        var cancelled = await UserWithAsync(UserRoles.StayOwnerId, Row(Guid.Empty, PlayStates.Canceled, false, Now.AddDays(10)));
        var stuck = await UserWithAsync(UserRoles.StayOwnerId, Row(Guid.Empty, PlayStates.Active, true, Now.AddDays(-30)));
        await using var c = CreateContext();
        Assert.Empty(await Guard(c, new SubscriptionDeletionContext()).CheckAsync(cancelled));
        Assert.Empty(await Guard(c, new SubscriptionDeletionContext()).CheckAsync(stuck));
    }

    [IntegrationFact]
    public async Task Token_gone_at_google_never_blocks_and_a_failed_sync_adds_the_hint()
    {
        var row = Row(Guid.Empty, PlayStates.Active, true, Now.AddDays(20));
        var user = await UserWithAsync(UserRoles.SeekerId, row);

        await using (var c = CreateContext())
        {
            var ctx = new SubscriptionDeletionContext();
            var sync = new FakeSync(_ => Task.FromResult(new PlaySyncResult(PlaySyncOutcome.ApiError, null, null, null, null, null, null,
                PlayApiErrorKind.NotFound, null, null)));
            await new SubscriptionDeletionPreCheck(c, ctx, Services(sync), TimeProvider.System, NullLogger<SubscriptionDeletionPreCheck>.Instance)
                .RefreshAsync(user);
            Assert.Equal(1, sync.Calls);
            Assert.Contains(row.Id, ctx.GoneSubscriptionIds);
            Assert.Empty(await Guard(c, ctx).CheckAsync(user));
        }

        await using (var c = CreateContext())
        {
            var ctx = new SubscriptionDeletionContext();
            var sync = new FakeSync(_ => throw new HttpRequestException("down"));
            await new SubscriptionDeletionPreCheck(c, ctx, Services(sync), TimeProvider.System, NullLogger<SubscriptionDeletionPreCheck>.Instance)
                .RefreshAsync(user);
            Assert.True(ctx.SyncUnsure);
            var block = Assert.Single(await Guard(c, ctx).CheckAsync(user));
            Assert.EndsWith(AccountDeletionReasons.JustCancelledHint, block.Message);
        }

        await using (var c = CreateContext())
        {
            var ctx = new SubscriptionDeletionContext();
            await new SubscriptionDeletionPreCheck(c, ctx, Services(null), TimeProvider.System, NullLogger<SubscriptionDeletionPreCheck>.Instance)
                .RefreshAsync(user);
            Assert.True(ctx.SyncUnsure);
        }
    }

    [IntegrationFact]
    public async Task Sync_that_turns_the_row_cancelled_lets_the_deletion_through()
    {
        var row = Row(Guid.Empty, PlayStates.Active, true, Now.AddDays(20));
        var user = await UserWithAsync(UserRoles.StayOwnerId, row);
        await using var c = CreateContext();
        var ctx = new SubscriptionDeletionContext();
        var sync = new FakeSync(async _ =>
        {
            await using var w = CreateContext();
            await w.PlaySubscriptions.Where(s => s.Id == row.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, PlayStates.Canceled).SetProperty(s => s.AutoRenewEnabled, false));
            return new PlaySyncResult(PlaySyncOutcome.Cancelled, row.Id, null, null, user, ListingKinds.Stay, null, null, null, Now.AddDays(20));
        });
        await new SubscriptionDeletionPreCheck(c, ctx, Services(sync), TimeProvider.System, NullLogger<SubscriptionDeletionPreCheck>.Instance)
            .RefreshAsync(user);
        Assert.False(ctx.SyncUnsure);
        Assert.Empty(await Guard(c, ctx).CheckAsync(user));
    }

    [IntegrationFact]
    public async Task No_blocking_row_means_no_google_call()
    {
        var user = await UserWithAsync(UserRoles.SeekerId, Row(Guid.Empty, PlayStates.Canceled, false, Now.AddDays(5)));
        await using var c = CreateContext();
        var sync = new FakeSync(_ => throw new InvalidOperationException("must not be called"));
        await new SubscriptionDeletionPreCheck(c, new SubscriptionDeletionContext(), Services(sync), TimeProvider.System,
            NullLogger<SubscriptionDeletionPreCheck>.Instance).RefreshAsync(user);
        Assert.Equal(0, sync.Calls);
    }

}

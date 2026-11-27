using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

    private sealed class Services(object? sync) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IPlayBillingSyncService) ? sync : null;
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
            await new SubscriptionDeletionPreCheck(c, ctx, new Services(sync), TimeProvider.System, NullLogger<SubscriptionDeletionPreCheck>.Instance)
                .RefreshAsync(user);
            Assert.Equal(1, sync.Calls);
            Assert.Contains(row.Id, ctx.GoneSubscriptionIds);
            Assert.Empty(await Guard(c, ctx).CheckAsync(user));
        }

        await using (var c = CreateContext())
        {
            var ctx = new SubscriptionDeletionContext();
            var sync = new FakeSync(_ => throw new HttpRequestException("down"));
            await new SubscriptionDeletionPreCheck(c, ctx, new Services(sync), TimeProvider.System, NullLogger<SubscriptionDeletionPreCheck>.Instance)
                .RefreshAsync(user);
            Assert.True(ctx.SyncUnsure);
            var block = Assert.Single(await Guard(c, ctx).CheckAsync(user));
            Assert.EndsWith(AccountDeletionReasons.JustCancelledHint, block.Message);
        }

        await using (var c = CreateContext())
        {
            var ctx = new SubscriptionDeletionContext();
            await new SubscriptionDeletionPreCheck(c, ctx, new Services(null), TimeProvider.System, NullLogger<SubscriptionDeletionPreCheck>.Instance)
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
        await new SubscriptionDeletionPreCheck(c, ctx, new Services(sync), TimeProvider.System, NullLogger<SubscriptionDeletionPreCheck>.Instance)
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
        await new SubscriptionDeletionPreCheck(c, new SubscriptionDeletionContext(), new Services(sync), TimeProvider.System,
            NullLogger<SubscriptionDeletionPreCheck>.Instance).RefreshAsync(user);
        Assert.Equal(0, sync.Calls);
    }

}

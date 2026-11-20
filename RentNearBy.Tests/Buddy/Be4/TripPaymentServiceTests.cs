using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;

namespace RentNearBy.Tests.Buddy.Be4;

// E9, E10, E12, E18, E19: success, errors, replays, races, notices and pushes, and fixed statement counts (3 and 30 rows).
// Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it.
public class TripPaymentServiceTests(TestDatabase db) : IntegrationTestBase(db)
{
    private Guid _trip;
    private Guid _owner;
    private Guid _aman;
    private Guid _priya;
    private Guid _stranger;
    private int _amanOwes;

    private static TripPaymentService Service(ApplicationDbContext ctx, RecordingLiveHints? hints = null) => new(
        ctx, new TripAccess(ctx), new TripLedger(ctx, NullLogger<TripLedger>.Instance), new NoticeWriter(ctx), new NoticeReader(ctx),
        hints ?? new RecordingLiveHints(), TimeProvider.System, NullLogger<TripPaymentService>.Instance);

    // Owner pays every expense; owner, Aman and Priya share each one equally, so Aman and Priya owe the owner.
    private async Task SeedAsync(ApplicationDbContext ctx, int expenses)
    {
        var people = await BuddySeed.UsersAsync(ctx, 4);
        (_owner, _aman, _priya, _stranger) = (people[0], people[1], people[2], people[3]);
        _trip = await BuddySeed.TripAsync(ctx, _owner);
        await BuddySeed.MembersAsync(ctx, _trip, _owner, [_aman, _priya]);
        await BuddySeed.ExpensesAsync(ctx, _trip, _owner, expenses, [_owner, _aman, _priya], amount: 900);
        var snapshot = await new TripLedger(ctx, NullLogger<TripLedger>.Instance).LoadAsync(_trip, [_owner, _aman, _priya]);
        _amanOwes = snapshot.Open.Single(o => o.FromUserId == _aman).Amount;
    }

    private SaidPaidRequestDto Pay(Guid? op = null, int? amount = null) => new() { OpId = op ?? Guid.NewGuid(), ToUserId = _owner, Amount = amount ?? _amanOwes };

    private async Task<List<UserNotice>> NoticesAsync(Guid user)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<UserNotice>().AsNoTracking().Where(n => n.UserId == user && n.TripId == _trip).ToListAsync();
    }

    private async Task<List<OutboxMessage>> OutboxAsync(string prefix)
    {
        await using var ctx = CreateContext();
        return await ctx.OutboxMessages.AsNoTracking().Where(o => o.Key.StartsWith(prefix)).ToListAsync();
    }

    [IntegrationFact]
    public async Task SaidPaid_stores_the_row_tells_the_owner_and_clears_the_payer_reminders()
    {
        await using (var ctx = CreateContext())
        {
            await SeedAsync(ctx, 3);
            await BuddySeed.NoticesAsync(ctx, _aman, [(NoticeKinds.SettleReminder, _trip), (NoticeKinds.PaymentNotReceived, _trip)]);
        }
        var hints = new RecordingLiveHints();
        var op = Guid.NewGuid();
        await using var c = CreateContext();
        var result = await Service(c, hints).SaidPaidAsync(_trip, _aman, Pay(op));

        Assert.Null(result.Error);
        Assert.True(result.Created);
        Assert.Equal(PaymentStates.SaidPaid, result.Payment!.State);
        Assert.Equal(op, result.Payment.PaymentId);
        Assert.True(result.Payment.CanWithdraw);
        Assert.Single(hints.TripExpensesChanged);
        var ownerNotice = Assert.Single(await NoticesAsync(_owner));
        Assert.Equal(NoticeKinds.PaymentSaidPaid, ownerNotice.Kind);
        Assert.Equal(_amanOwes, ownerNotice.Amount);
        Assert.Equal(_aman, ownerNotice.ActorUserId);
        Assert.Empty(await NoticesAsync(_aman));
        Assert.Single(await OutboxAsync($"paid:{op:N}:{_owner:N}"));
    }

    [IntegrationFact]
    public async Task SaidPaid_replay_answers_the_same_row_and_a_second_op_is_nothing_to_pay()
    {
        await using (var ctx = CreateContext()) await SeedAsync(ctx, 3);
        var op = Guid.NewGuid();
        await using var c = CreateContext();
        var first = await Service(c).SaidPaidAsync(_trip, _aman, Pay(op));
        var replay = await Service(c).SaidPaidAsync(_trip, _aman, Pay(op));
        var other = await Service(c).SaidPaidAsync(_trip, _aman, Pay());

        Assert.True(first.Created);
        Assert.Null(replay.Error);
        Assert.False(replay.Created);
        Assert.Equal(op, replay.Payment!.PaymentId);
        Assert.Equal(first.BalancesVersion, replay.BalancesVersion);
        Assert.Equal(BuddyErrorTypes.NothingToPay, other.Error);
        Assert.Single(await NoticesAsync(_owner));
        Assert.Equal(1, (await NoticesAsync(_owner))[0].Count);
    }

    [IntegrationFact]
    public async Task SaidPaid_two_phones_at_once_store_one_row()
    {
        await using (var ctx = CreateContext()) await SeedAsync(ctx, 3);
        await using var a = CreateContext();
        await using var b = CreateContext();
        var results = await Task.WhenAll(Service(a).SaidPaidAsync(_trip, _aman, Pay()), Service(b).SaidPaidAsync(_trip, _aman, Pay()));

        Assert.Single(results, r => r.Error is null);
        Assert.Single(results, r => r.Error == BuddyErrorTypes.NothingToPay);
        await using var check = CreateContext();
        Assert.Equal(1, await check.Set<TripPayment>().CountAsync(p => p.TripId == _trip && p.State == PaymentStates.SaidPaid));
    }

    [IntegrationFact]
    public async Task SaidPaid_errors()
    {
        await using (var ctx = CreateContext()) await SeedAsync(ctx, 3);
        await using var c = CreateContext();
        var s = Service(c);
        Assert.Equal(BuddyErrorTypes.BalancesChanged, (await s.SaidPaidAsync(_trip, _aman, Pay(amount: _amanOwes + 1))).Error);
        Assert.Equal(BuddyErrorTypes.NothingToPay, (await s.SaidPaidAsync(_trip, _owner, new SaidPaidRequestDto { OpId = Guid.NewGuid(), ToUserId = _aman, Amount = 10 })).Error);
        Assert.Equal(BuddyErrorTypes.TripNotFound, (await s.SaidPaidAsync(_trip, _stranger, Pay())).Error);
        Assert.Equal(BuddyErrorTypes.TripNotFound, (await s.SaidPaidAsync(Guid.NewGuid(), _aman, Pay())).Error);
        var changed = await s.SaidPaidAsync(_trip, _aman, Pay(amount: 1));
        Assert.Equal(_amanOwes, changed.ErrorData!.GetType().GetProperty("amount")!.GetValue(changed.ErrorData));
    }

    [IntegrationFact]
    public async Task Former_traveller_can_still_say_paid()
    {
        await using (var ctx = CreateContext())
        {
            await SeedAsync(ctx, 3);
            await ctx.Set<TripMember>().Where(m => m.TripId == _trip && m.UserId == _aman)
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, TripMemberStatuses.Left));
        }
        await using var c = CreateContext();
        Assert.Null((await Service(c).SaidPaidAsync(_trip, _aman, Pay())).Error);
    }

    [IntegrationFact]
    public async Task Received_is_final_owner_only_and_replays()
    {
        await using (var ctx = CreateContext()) await SeedAsync(ctx, 3);
        var op = Guid.NewGuid();
        await using var c = CreateContext();
        var s = Service(c);
        await s.SaidPaidAsync(_trip, _aman, Pay(op));

        Assert.Equal(BuddyErrorTypes.NotTripOwner, (await s.ReceivedAsync(_trip, _aman, op)).Error);
        Assert.Equal(BuddyErrorTypes.TripNotFound, (await s.ReceivedAsync(_trip, _stranger, op)).Error);
        Assert.Equal(BuddyErrorTypes.PaymentNotFound, (await s.ReceivedAsync(_trip, _owner, Guid.NewGuid())).Error);

        var received = await s.ReceivedAsync(_trip, _owner, op);
        Assert.Null(received.Error);
        Assert.Equal(PaymentStates.Received, received.Payment!.State);
        Assert.Equal(_owner, received.Payment.ReceivedByUserId);
        var again = await s.ReceivedAsync(_trip, _owner, op);
        Assert.Null(again.Error);
        Assert.Equal(received.BalancesVersion, again.BalancesVersion);
        Assert.Empty(await NoticesAsync(_owner));
        Assert.Single(await OutboxAsync($"recv:{op:N}:{_aman:N}"));

        var withdraw = await s.WithdrawAsync(_trip, _aman, op);
        Assert.Equal(BuddyErrorTypes.PaymentStateChanged, withdraw.Error);
        var notReceived = await s.NotReceivedAsync(_trip, _owner, op);
        Assert.Equal(BuddyErrorTypes.PaymentStateChanged, notReceived.Error);
    }

    [IntegrationFact]
    public async Task Withdraw_reopens_the_pair_and_only_the_payer_may_do_it()
    {
        await using (var ctx = CreateContext()) await SeedAsync(ctx, 3);
        var op = Guid.NewGuid();
        await using var c = CreateContext();
        var s = Service(c);
        await s.SaidPaidAsync(_trip, _aman, Pay(op));

        Assert.Equal(BuddyErrorTypes.NotYourDebt, (await s.WithdrawAsync(_trip, _priya, op)).Error);
        var w = await s.WithdrawAsync(_trip, _aman, op);
        Assert.Null(w.Error);
        Assert.Equal(PaymentStates.Open, w.Payment!.State);
        Assert.Null(w.Payment.PaymentId);
        Assert.Equal(_amanOwes, w.Payment.Amount);
        Assert.True(w.Payment.CanSayPaid);
        Assert.Empty(await NoticesAsync(_owner));
        Assert.Single(await OutboxAsync($"wd:{op:N}:{_owner:N}"));
        Assert.Null((await s.WithdrawAsync(_trip, _aman, op)).Error);

        var late = await s.ReceivedAsync(_trip, _owner, op);
        Assert.Equal(BuddyErrorTypes.PaymentStateChanged, late.Error);
    }

    [IntegrationFact]
    public async Task Withdraw_and_received_race_one_wins()
    {
        await using (var ctx = CreateContext()) await SeedAsync(ctx, 3);
        var op = Guid.NewGuid();
        await using (var c0 = CreateContext()) await Service(c0).SaidPaidAsync(_trip, _aman, Pay(op));
        await using var a = CreateContext();
        await using var b = CreateContext();
        var results = await Task.WhenAll(Service(a).WithdrawAsync(_trip, _aman, op), Service(b).ReceivedAsync(_trip, _owner, op));
        Assert.Single(results, r => r.Error is null);
        Assert.Single(results, r => r.Error == BuddyErrorTypes.PaymentStateChanged);
    }

    [IntegrationFact]
    public async Task Not_received_tells_the_payer_and_marks_the_open_row()
    {
        await using (var ctx = CreateContext()) await SeedAsync(ctx, 3);
        var op = Guid.NewGuid();
        await using var c = CreateContext();
        var s = Service(c);
        await s.SaidPaidAsync(_trip, _aman, Pay(op));

        Assert.Equal(BuddyErrorTypes.NotTripOwner, (await s.NotReceivedAsync(_trip, _aman, op)).Error);
        var r = await s.NotReceivedAsync(_trip, _owner, op);
        Assert.Null(r.Error);
        Assert.Equal(PaymentStates.Open, r.Payment!.State);
        Assert.NotNull(r.Payment.AfterNotReceivedAt);
        Assert.True(r.Payment.CanRemind);
        var notice = Assert.Single(await NoticesAsync(_aman));
        Assert.Equal(NoticeKinds.PaymentNotReceived, notice.Kind);
        Assert.Equal(_amanOwes, notice.Amount);
        Assert.Empty(await NoticesAsync(_owner));
        Assert.Single(await OutboxAsync($"notrecv:{op:N}"));
        Assert.Null((await s.NotReceivedAsync(_trip, _owner, op)).Error);

        await s.SaidPaidAsync(_trip, _aman, Pay());
        Assert.Empty(await NoticesAsync(_aman));
    }

    [IntegrationFact]
    public async Task Remind_once_per_payer_per_day()
    {
        await using (var ctx = CreateContext()) await SeedAsync(ctx, 3);
        await using var c = CreateContext();
        var s = Service(c);

        Assert.Equal(BuddyErrorTypes.NotTripOwner, await s.RemindAsync(_trip, _aman, _priya));
        Assert.Equal(BuddyErrorTypes.TripNotFound, await s.RemindAsync(_trip, _stranger, _aman));
        Assert.Equal(BuddyErrorTypes.NothingToPay, await s.RemindAsync(_trip, _owner, _owner));
        Assert.Equal(BuddyErrorTypes.NothingToPay, await s.RemindAsync(_trip, _owner, _stranger));
        Assert.Null(await s.RemindAsync(_trip, _owner, _aman));
        Assert.Equal(BuddyErrorTypes.ReminderAlreadySent, await s.RemindAsync(_trip, _owner, _aman));

        var notice = Assert.Single(await NoticesAsync(_aman));
        Assert.Equal(NoticeKinds.SettleReminder, notice.Kind);
        Assert.Equal(_amanOwes, notice.Amount);
        Assert.Single(await OutboxAsync($"remind:{_trip:N}:{_aman:N}:"));

        await s.SaidPaidAsync(_trip, _aman, Pay());
        Assert.Equal(BuddyErrorTypes.NothingToPay, await s.RemindAsync(_trip, _owner, _aman));
    }

    [IntegrationFact]
    public async Task Notice_rows_are_upserted_in_place_and_reads_restart_the_count()
    {
        await using (var ctx = CreateContext()) await SeedAsync(ctx, 3);
        await using (var c = CreateContext())
        {
            var w = new NoticeWriter(c);
            await w.UpsertAsync([new NoticeUpsert(_aman, NoticeKinds.ExpensesChanged, _trip, _owner)]);
            await w.UpsertAsync([new NoticeUpsert(_aman, NoticeKinds.ExpensesChanged, _trip, _owner), new NoticeUpsert(_stranger, NoticeKinds.ExpensesChanged, _trip, _owner)]);
            await w.UpsertAsync([new NoticeUpsert(Guid.NewGuid(), NoticeKinds.ExpensesChanged, _trip, _owner)]);
        }
        var row = Assert.Single(await NoticesAsync(_aman));
        Assert.Equal(2, row.Count);

        await using (var c = CreateContext())
        {
            var reader = new NoticeReader(c);
            await reader.MarkReadAsync(row.Id, _aman);
            Assert.Equal(0, await reader.UnreadCountAsync(_aman));
            await new NoticeWriter(c).UpsertAsync([new NoticeUpsert(_aman, NoticeKinds.ExpensesChanged, _trip, _owner)]);
            Assert.Equal(1, await reader.UnreadCountAsync(_aman));
        }
        row = Assert.Single(await NoticesAsync(_aman));
        Assert.Equal(1, row.Count);
        Assert.Null(row.ReadAt);
    }

    [IntegrationFact]
    public async Task Coalesced_pushes_write_one_outbox_row_per_key_with_the_delay()
    {
        await using (var ctx = CreateContext()) await SeedAsync(ctx, 3);
        var now = DateTime.UtcNow;
        await using (var c = CreateContext())
        {
            var w = new NoticeWriter(c);
            await w.PushAsync([BuddyPushCopy.TripExpensesUpdated(_aman, _trip, "Rahul", "T", now)]);
            await w.PushAsync([BuddyPushCopy.TripExpensesUpdated(_aman, _trip, "Rahul", "T", now), BuddyPushCopy.TripExpensesUpdated(_priya, _trip, "Rahul", "T", now)]);
        }
        var rows = await OutboxAsync($"exp:{_trip:N}:");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.NextAttemptAt > r.CreatedAt.AddSeconds(50)));
    }

    [IntegrationFact]
    public async Task Guard_lists_open_money_and_is_one_statement_when_nothing_is_open()
    {
        await using (var ctx = CreateContext()) await SeedAsync(ctx, 3);
        await using (var c = CreateContext()) await new TripLedger(c, NullLogger<TripLedger>.Instance).RefreshStateAsync(_trip, [_owner, _aman, _priya], null);
        await using var counting = Db.CreateCountingContext(out var counter);
        var guard = new TripMoneyGuard(counting, new TripAccess(counting), new TripLedger(counting, NullLogger<TripLedger>.Instance));
        var lines = await guard.GetOpenPaymentsAsync(_trip);
        Assert.Equal(2, lines.Count);
        Assert.Equal(5, counter.Count);

        counter.Reset();
        Assert.Empty(await guard.GetOpenPaymentsAsync(Guid.NewGuid()));
        Assert.Equal(1, counter.Count);
    }

    [IntegrationFact]
    public async Task Account_deletion_is_blocked_by_open_money_on_a_current_trip()
    {
        await using (var ctx = CreateContext()) await SeedAsync(ctx, 3);
        await using (var c = CreateContext()) await new TripLedger(c, NullLogger<TripLedger>.Instance).RefreshStateAsync(_trip, [_owner, _aman, _priya], null);
        await using var counting = Db.CreateCountingContext(out var counter);
        var participant = new MoneyAccountDeletion(counting, new TripLedger(counting, NullLogger<TripLedger>.Instance), TimeProvider.System);
        var blocks = await participant.CheckAsync(_aman);
        Assert.Equal(AccountDeletionReasons.SharedTripOpenBalance, Assert.Single(blocks).Code);
        Assert.Equal(4, counter.Count);
        Assert.Empty(await participant.CheckAsync(_stranger));
    }

    [IntegrationFact]
    public async Task Account_deletion_of_the_owner_is_blocked_while_anything_is_open_whatever_his_own_net()
    {
        await using (var ctx = CreateContext()) await SeedAsync(ctx, 3);
        await using (var c = CreateContext()) await new TripLedger(c, NullLogger<TripLedger>.Instance).RefreshStateAsync(_trip, [_owner, _aman, _priya], null);
        await using var counting = Db.CreateCountingContext(out var counter);
        var participant = new MoneyAccountDeletion(counting, new TripLedger(counting, NullLogger<TripLedger>.Instance), TimeProvider.System);
        Assert.Equal(AccountDeletionReasons.SharedTripOpenBalance, Assert.Single(await participant.CheckAsync(_owner)).Code);
        Assert.Equal(1, counter.Count);
    }

    [IntegrationFact]
    public async Task Morning_job_reminds_payers_once_a_day()
    {
        await using (var ctx = CreateContext())
        {
            await SeedAsync(ctx, 3);
            var end = IstTime.TodayIst(DateTime.UtcNow).AddDays(-3);
            await ctx.Trips.Where(t => t.Id == _trip).ExecuteUpdateAsync(u => u.SetProperty(t => t.EndDate, end).SetProperty(t => t.StartDate, end.AddDays(-2)));
            await new TripLedger(ctx, NullLogger<TripLedger>.Instance).RefreshStateAsync(_trip, [_owner, _aman, _priya], null);
        }
        await using var c = CreateContext();
        var job = new SettleReminderJob(c, new TripAccess(c), new TripLedger(c, NullLogger<TripLedger>.Instance), new NoticeWriter(c), TimeProvider.System,
            NullLogger<SettleReminderJob>.Instance);
        Assert.True(await job.RunAsync(CancellationToken.None) >= 2);
        await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, Assert.Single(await NoticesAsync(_aman)).Count);
        Assert.Single(await OutboxAsync($"autoremind:{_trip:N}:{_aman:N}:3"));
    }

    // Query budgets (contract 7.1): the same count with 3 and with 30 expenses.
    [IntegrationFact]
    public Task SaidPaid_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAsync,
        async ctx => Assert.Null((await Service(ctx).SaidPaidAsync(_trip, _aman, Pay())).Error), budget: 12);

    [IntegrationFact]
    public Task Received_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedWithSaidPaidAsync,
        async ctx => Assert.Null((await Service(ctx).ReceivedAsync(_trip, _owner, _paid)).Error), budget: 11);

    [IntegrationFact]
    public Task Withdraw_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedWithSaidPaidAsync,
        async ctx => Assert.Null((await Service(ctx).WithdrawAsync(_trip, _aman, _paid)).Error), budget: 11);

    [IntegrationFact]
    public Task NotReceived_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedWithSaidPaidAsync,
        async ctx => Assert.Null((await Service(ctx).NotReceivedAsync(_trip, _owner, _paid)).Error), budget: 11);

    [IntegrationFact]
    public Task Remind_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAsync,
        async ctx => Assert.Null(await Service(ctx).RemindAsync(_trip, _owner, _aman)), budget: 7);

    [IntegrationFact]
    public Task Notices_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedNoticesAsync,
        async ctx => Assert.NotEmpty((await new NoticeReader(ctx).PageAsync(_aman, null, BuddyLimits.NoticesPageDefault)).Items), budget: 2);

    private Guid _paid;

    private async Task SeedWithSaidPaidAsync(ApplicationDbContext ctx, int expenses)
    {
        await SeedAsync(ctx, expenses);
        _paid = Guid.NewGuid();
        Assert.Null((await Service(ctx).SaidPaidAsync(_trip, _aman, Pay(_paid))).Error);
    }

    // rows notices of different trips for Aman.
    private async Task SeedNoticesAsync(ApplicationDbContext ctx, int rows)
    {
        var people = await BuddySeed.UsersAsync(ctx, 2);
        (_owner, _aman) = (people[0], people[1]);
        var trips = new List<(string, Guid)>();
        for (var i = 0; i < rows; i++) trips.Add((NoticeKinds.ExpensesChanged, await BuddySeed.TripAsync(ctx, _owner, title: $"Trip {i}")));
        await BuddySeed.NoticesAsync(ctx, _aman, trips);
    }
}

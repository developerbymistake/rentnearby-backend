using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Entities.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be3;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Support.Buddy;

namespace RentNearBy.Tests.Buddy.Be3;

// Needs RENTNEARBY_TEST_DB (PostGIS); skipped without it. Query budgets of contract 7.1 (same count with few and many rows)
// and the behaviour, errors and races of E1 to E8 and E17, on the service methods the handlers call.
public class Be3IntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private Guid _trip;
    private Guid _owner;
    private List<Guid> _people = [];
    private List<Guid> _expenses = [];

    private sealed record Writer(TripExpenseService Service, RecordingNoticeWriter Notices, RecordingLiveHints Hints);

    private static Writer NewWriter(ApplicationDbContext ctx)
    {
        var notices = new RecordingNoticeWriter();
        var hints = new RecordingLiveHints();
        var service = new TripExpenseService(ctx, new TripAccess(ctx), new TripLedger(ctx, NullLogger<TripLedger>.Instance), notices, hints,
            TimeProvider.System, NullLogger<TripExpenseService>.Instance);
        return new Writer(service, notices, hints);
    }

    private static TripExpenseReader NewReader(ApplicationDbContext ctx)
        => new(ctx, new TripAccess(ctx), new TripExpenseSuggestions(ctx), TimeProvider.System);

    private static TripBalanceReader NewBalances(ApplicationDbContext ctx)
        => new(ctx, new TripAccess(ctx), new TripLedger(ctx, NullLogger<TripLedger>.Instance), TimeProvider.System);

    // rows = expenses; travellers capped at 12 (owner + 11 viewers).
    private async Task SeedAsync(ApplicationDbContext ctx, int rows)
    {
        var people = await BuddySeed.UsersAsync(ctx, Math.Min(rows, 11) + 1);
        _owner = people[0];
        _trip = await BuddySeed.TripAsync(ctx, _owner, days: 5);
        await BuddySeed.MembersAsync(ctx, _trip, _owner, people.Skip(1));
        _expenses = await BuddySeed.ExpensesAsync(ctx, _trip, _owner, rows, people, days: 5);
        await new TripLedger(ctx, NullLogger<TripLedger>.Instance).RefreshStateAsync(_trip, people, null);
        _people = people;
    }

    // n viewers, no expenses.
    private async Task SeedTravellersAsync(ApplicationDbContext ctx, int viewers)
    {
        var people = await BuddySeed.UsersAsync(ctx, viewers + 1);
        _owner = people[0];
        _trip = await BuddySeed.TripAsync(ctx, _owner, days: 5);
        await BuddySeed.MembersAsync(ctx, _trip, _owner, people.Skip(1));
        _people = people;
        _expenses = [];
    }

    private SaveExpenseRequestDto NewExpense(int amount = 1000, IEnumerable<Guid>? sharers = null, int baseVersion = 0) => new()
    {
        OpId = Guid.NewGuid(), BaseVersion = baseVersion, Amount = amount, Title = "Dinner by the Ganga", Category = ExpenseCategories.Food,
        DayNumber = 1, PaidByUserId = _owner, SplitMode = SplitModes.Chosen, Sharers = (sharers ?? _people).ToList(),
    };

    [IntegrationFact]
    public Task E1_owner_first_page_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAsync,
        async ctx => Assert.True((await NewReader(ctx).ListAsync(_trip, _owner, null, 100)).IsOk), budget: 9);

    [IntegrationFact]
    public Task E1_viewer_first_page_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAsync,
        async ctx => Assert.True((await NewReader(ctx).ListAsync(_trip, _people[1], null, 100)).IsOk), budget: 6);

    [IntegrationFact]
    public Task E1_next_page_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAsync,
        async ctx => Assert.True((await NewReader(ctx).ListAsync(_trip, _owner, (5, DateTime.UtcNow.AddDays(1), Guid.Empty), 100)).IsOk), budget: 2);

    [IntegrationFact]
    public Task E4_detail_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAsync,
        async ctx => Assert.True((await NewReader(ctx).DetailAsync(_trip, _expenses[0], _owner)).IsOk), budget: 5);

    [IntegrationFact]
    public Task E8_balances_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAsync,
        async ctx => Assert.True((await NewBalances(ctx).BalancesAsync(_trip, _owner)).IsOk), budget: 6);

    [IntegrationFact]
    public Task E17_summary_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAsync,
        async ctx => Assert.True((await NewBalances(ctx).SummaryAsync(_trip, _owner)).IsOk), budget: 6);

    [IntegrationFact]
    public Task MoneyCards_cards_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAsync,
        async ctx => Assert.Single(await new TripMoneyCards(ctx, new TripLedger(ctx, NullLogger<TripLedger>.Instance)).GetCardsAsync([_trip], _owner)), budget: 2);

    [IntegrationFact]
    public Task MoneyCards_standings_QueryCount_IsFixed() => QueryAssert.SameCountAsync(Db, SeedAsync,
        async ctx => Assert.Single(await new TripMoneyCards(ctx, new TripLedger(ctx, NullLogger<TripLedger>.Instance)).GetStandingsAsync([_trip], _people[1])), budget: 4);

    // Write budgets: the recording writer sends no SQL, so its calls are added to the counted statements.
    [IntegrationFact]
    public async Task E2_create_QueryCount_IsFixed_for_1_and_10_sharers()
    {
        var counts = new List<int>();
        foreach (var viewers in new[] { 1, 10 })
        {
            await using (var plain = CreateContext()) await SeedTravellersAsync(plain, viewers);
            await using var ctx = Db.CreateCountingContext(out var counter);
            var w = NewWriter(ctx);
            counter.Reset();
            Assert.True((await w.Service.SaveAsync(_trip, Guid.NewGuid(), _owner, NewExpense())).IsOk);
            counts.Add(counter.Count + w.Notices.Calls);
        }
        Assert.Equal(counts[0], counts[1]);
        Assert.True(counts[1] <= 12, $"E2 used {counts[1]} statements");
    }

    [IntegrationFact]
    public async Task E3_delete_QueryCount_IsFixed()
    {
        var counts = new List<int>();
        foreach (var rows in new[] { 1, 10 })
        {
            await using (var plain = CreateContext()) await SeedAsync(plain, rows);
            await using var ctx = Db.CreateCountingContext(out var counter);
            var w = NewWriter(ctx);
            counter.Reset();
            Assert.True((await w.Service.DeleteAsync(_trip, _expenses[0], _owner, 1, Guid.NewGuid(), false)).IsOk);
            counts.Add(counter.Count + w.Notices.Calls);
        }
        Assert.Equal(counts[0], counts[1]);
        Assert.True(counts[1] <= 11, $"E3 used {counts[1]} statements");
    }

    [IntegrationFact]
    public async Task E7_share_with_QueryCount_IsFixed_for_1_and_10_items()
    {
        var counts = new List<int>();
        foreach (var items in new[] { 1, 10 })
        {
            Guid joiner;
            await using (var plain = CreateContext())
            {
                await SeedAsync(plain, items);
                var late = await BuddySeed.UsersAsync(plain, 1, "Late");
                joiner = late[0];
                await BuddySeed.MembersAsync(plain, _trip, _owner, late);
            }
            await using var ctx = Db.CreateCountingContext(out var counter);
            var w = NewWriter(ctx);
            counter.Reset();
            var result = await w.Service.ShareWithAsync(_trip, _owner, new ShareWithRequestDto
            {
                OpId = Guid.NewGuid(), UserId = joiner, Items = _expenses.Select(id => new ShareWithItemDto { ExpenseId = id, BaseVersion = 1 }).ToList(),
            });
            Assert.True(result.IsOk, result.Error);
            counts.Add(counter.Count + w.Notices.Calls);
        }
        Assert.Equal(counts[0], counts[1]);
        Assert.True(counts[1] <= 14, $"E7 used {counts[1]} statements");
    }

    [IntegrationFact]
    public async Task E2_create_replay_and_id_taken()
    {
        await using (var plain = CreateContext()) await SeedTravellersAsync(plain, 2);
        await using var ctx = CreateContext();
        var w = NewWriter(ctx);
        var id = Guid.NewGuid();
        var request = NewExpense(18500);
        var first = await w.Service.SaveAsync(_trip, id, _owner, request);
        Assert.True(first.IsOk && first.Created);
        Assert.Equal(new[] { 6168, 6166, 6166 }, first.Value!.Expense.Shares.Select(s => s.Share));
        Assert.Equal(_owner, first.Value.Expense.LeftoverUserId);
        Assert.Equal(1, first.Value.BalancesVersion);
        Assert.Equal(2, w.Notices.Upserts.Count(n => n.Kind == NoticeKinds.ExpensesChanged));
        Assert.DoesNotContain(w.Notices.Upserts, n => n.UserId == _owner);
        Assert.Equal(2, w.Notices.Pushes.Count);
        Assert.Single(w.Hints.TripExpensesChanged);

        var replay = await NewWriter(ctx).Service.SaveAsync(_trip, id, _owner, request);
        Assert.True(replay.IsOk);
        Assert.False(replay.Created);
        Assert.Equal(1, replay.Value!.Expense.Version);

        var other = NewExpense(18500);
        Assert.Equal(BuddyErrorTypes.ExpenseIdTaken, (await w.Service.SaveAsync(_trip, id, _owner, other)).Error);
        Assert.Equal(1, await ctx.Set<TripExpense>().CountAsync(e => e.TripId == _trip));
        Assert.Equal(1, await ctx.Set<TripExpenseRevision>().CountAsync(r => r.ExpenseId == id));
    }

    [IntegrationFact]
    public async Task E2_edit_conflict_replay_and_history()
    {
        await using (var plain = CreateContext()) await SeedTravellersAsync(plain, 2);
        await using var ctx = CreateContext();
        var w = NewWriter(ctx);
        var id = Guid.NewGuid();
        Assert.True((await w.Service.SaveAsync(_trip, id, _owner, NewExpense(19200))).IsOk);

        var edit = NewExpense(18500, baseVersion: 1);
        var edited = await w.Service.SaveAsync(_trip, id, _owner, edit);
        Assert.True(edited.IsOk);
        Assert.Equal(2, edited.Value!.Expense.Version);
        Assert.Equal(1, edited.Value.Expense.EditedTimes);

        var replay = await w.Service.SaveAsync(_trip, id, _owner, edit);
        Assert.True(replay.IsOk);
        Assert.Equal(2, replay.Value!.Expense.Version);

        var stale = NewExpense(18000, baseVersion: 1);
        var conflict = await w.Service.SaveAsync(_trip, id, _owner, stale);
        Assert.Equal(BuddyErrorTypes.ExpenseVersionConflict, conflict.Error);
        var current = Assert.IsType<ExpenseConflictDto>(conflict.ErrorData).Current!;
        Assert.Equal(18500, current.Amount);
        Assert.Equal(2, current.Version);

        var detail = await NewReader(ctx).DetailAsync(_trip, id, _owner);
        Assert.Equal(new[] { RevisionKinds.Edited, RevisionKinds.Added }, detail.Value!.History.Select(h => h.Kind));
        Assert.Equal("amount", detail.Value.History[0].Changes[0].GetProperty("field").GetString());
        Assert.True(detail.Value.CanEdit);
    }

    [IntegrationFact]
    public async Task E2_refusals_viewer_former_sharer_payments_and_frozen()
    {
        await using (var plain = CreateContext())
        {
            await SeedTravellersAsync(plain, 2);
            var left = await BuddySeed.UsersAsync(plain, 1, "Left");
            await BuddySeed.MembersAsync(plain, _trip, _owner, left, TripMemberStatuses.Left);
            _people.Add(left[0]);
        }
        await using var ctx = CreateContext();
        var w = NewWriter(ctx);
        Assert.Equal(BuddyErrorTypes.NotTripOwner, (await w.Service.SaveAsync(_trip, Guid.NewGuid(), _people[1], NewExpense())).Error);
        Assert.Equal(BuddyErrorTypes.TripNotFound, (await w.Service.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), _owner, NewExpense())).Error);
        Assert.Equal(BuddyErrorTypes.SharerNotTraveller, (await w.Service.SaveAsync(_trip, Guid.NewGuid(), _owner, NewExpense())).Error);

        var current = _people.Take(3).ToList();
        var big = NewExpense(9999);
        big.DayNumber = 6;
        big.Sharers = current;
        Assert.Equal(BuddyErrorTypes.InvalidDay, (await w.Service.SaveAsync(_trip, Guid.NewGuid(), _owner, big)).Error);

        var id = Guid.NewGuid();
        Assert.True((await w.Service.SaveAsync(_trip, id, _owner, NewExpense(900, current))).IsOk);
        await BuddySeed.PaymentAsync(ctx, _trip, _people[1], _owner, 300, PaymentStates.Received);
        await new TripLedger(ctx, NullLogger<TripLedger>.Instance).RefreshStateAsync(_trip, current, null);
        var edit = NewExpense(1200, current, baseVersion: 1);
        Assert.Equal(BuddyErrorTypes.PaymentsNotAcknowledged, (await w.Service.SaveAsync(_trip, id, _owner, edit)).Error);
        edit.PaymentsAcknowledged = true;
        Assert.True((await w.Service.SaveAsync(_trip, id, _owner, edit)).IsOk);

        var balances = await NewBalances(ctx).BalancesAsync(_trip, _owner);
        Assert.NotNull(balances.Value!.Reopened);
        Assert.All(balances.Value.Payments.Where(p => p.State == PaymentStates.Open), p => Assert.True(p.AfterEdit));

        var past = await BuddySeed.TripAsync(ctx, _owner, days: 2, start: IstTime.TodayIst(DateTime.UtcNow).AddDays(-40));
        Assert.Equal(BuddyErrorTypes.TripExpensesFrozen, (await w.Service.SaveAsync(past, Guid.NewGuid(), _owner, NewExpense(100, [_owner]))).Error);
    }

    [IntegrationFact]
    public async Task E3_delete_keeps_history_and_shares_readable()
    {
        await using (var plain = CreateContext()) await SeedTravellersAsync(plain, 2);
        await using var ctx = CreateContext();
        var w = NewWriter(ctx);
        var id = Guid.NewGuid();
        Assert.True((await w.Service.SaveAsync(_trip, id, _owner, NewExpense(900))).IsOk);
        Assert.Equal(BuddyErrorTypes.ExpenseVersionConflict, (await w.Service.DeleteAsync(_trip, id, _owner, 5, Guid.NewGuid(), false)).Error);
        var op = Guid.NewGuid();
        var deleted = await w.Service.DeleteAsync(_trip, id, _owner, 1, op, false);
        Assert.True(deleted.IsOk);
        Assert.True((await w.Service.DeleteAsync(_trip, id, _owner, 1, op, false)).IsOk);
        Assert.Equal(BuddyErrorTypes.ExpenseNotFound, (await w.Service.DeleteAsync(_trip, id, _owner, 2, Guid.NewGuid(), false)).Error);
        Assert.Equal(0, await ctx.Set<TripExpenseShare>().CountAsync(s => s.ExpenseId == id));

        var detail = await NewReader(ctx).DetailAsync(_trip, id, _owner);
        Assert.True(detail.Value!.Expense.Deleted);
        Assert.Equal(3, detail.Value.Expense.Shares.Count);
        Assert.False(detail.Value.CanEdit);
        var list = await NewReader(ctx).ListAsync(_trip, _owner, null, 100);
        Assert.Empty(list.Value!.Items);
        Assert.Equal(0, list.Value.Totals!.Spent);
    }

    [IntegrationFact]
    public async Task E7_late_joiner_prompt_share_with_and_keep()
    {
        Guid late;
        await using (var plain = CreateContext())
        {
            await SeedTravellersAsync(plain, 1);
            var ids = await BuddySeed.ExpensesAsync(plain, _trip, _owner, 2, _people, days: 2, amount: 8000);
            _expenses = ids;
            await new TripLedger(plain, NullLogger<TripLedger>.Instance).RefreshStateAsync(_trip, _people, null);
            late = (await BuddySeed.UsersAsync(plain, 1, "Priya"))[0];
        }
        await using var ctx = CreateContext();
        await ctx.Set<TripMember>().AddAsync(new TripMember
        {
            Id = Guid.NewGuid(), TripId = _trip, UserId = late, Status = TripMemberStatuses.Joined, InvitedByUserId = _owner,
            InvitedAt = DateTime.UtcNow, InviteExpiresAt = DateTime.UtcNow.AddDays(14), JoinedAt = DateTime.UtcNow.AddMinutes(5),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var list = await NewReader(ctx).ListAsync(_trip, _owner, null, 100);
        var prompt = list.Value!.LatePrompt!;
        Assert.Equal(late, prompt.UserId);
        Assert.Equal(2, prompt.Count);
        Assert.Equal(16000, prompt.Total);
        Assert.Equal(2, prompt.Items[0].Shares.Count);

        var w = NewWriter(ctx);
        var shared = await w.Service.ShareWithAsync(_trip, _owner, new ShareWithRequestDto
        {
            OpId = Guid.NewGuid(), UserId = late, Items = [new ShareWithItemDto { ExpenseId = _expenses[0], BaseVersion = 1 }],
        });
        Assert.True(shared.IsOk, shared.Error);
        var expense = Assert.Single(shared.Value!.Expenses);
        Assert.Equal(SplitModes.Chosen, expense.SplitMode);
        Assert.Equal(new[] { 2668, 2666, 2666 }, expense.Shares.Select(s => s.Share));
        Assert.Contains(late, shared.Value.Notified);

        var again = await w.Service.ShareWithAsync(_trip, _owner, new ShareWithRequestDto
        {
            OpId = Guid.NewGuid(), UserId = late, Items = [new ShareWithItemDto { ExpenseId = _expenses[0], BaseVersion = 2 }],
        });
        Assert.Equal(BuddyErrorTypes.AlreadySharing, again.Error);
        var stale = await w.Service.ShareWithAsync(_trip, _owner, new ShareWithRequestDto
        {
            OpId = Guid.NewGuid(), UserId = late, Items = [new ShareWithItemDto { ExpenseId = _expenses[1], BaseVersion = 7 }],
        });
        Assert.Equal(BuddyErrorTypes.ExpenseVersionConflict, stale.Error);

        Assert.Null((await NewReader(ctx).ListAsync(_trip, _owner, null, 100)).Value!.LatePrompt);
        Assert.True((await w.Service.KeepAsync(_trip, _owner, late)).IsOk);
        Assert.Equal(BuddyErrorTypes.NotTripOwner, (await w.Service.KeepAsync(_trip, late, late)).Error);
    }

    [IntegrationFact]
    public async Task Former_sees_only_his_expenses_and_summary_is_refused()
    {
        await using (var plain = CreateContext()) await SeedAsync(plain, 3);
        await using (var plain = CreateContext())
            await plain.Set<TripMember>().Where(m => m.TripId == _trip && m.UserId == _people[1])
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, TripMemberStatuses.Left));
        await using var ctx = CreateContext();
        Assert.True((await NewWriter(ctx).Service.SaveAsync(_trip, Guid.NewGuid(), _owner, NewExpense(7000, [_owner, _people[2]]))).IsOk);
        var list = await NewReader(ctx).ListAsync(_trip, _people[1], null, 100);
        Assert.Equal("Former", list.Value!.Role);
        Assert.Equal(3, list.Value.Items.Count);
        Assert.Equal(3000, list.Value.Totals!.Spent);
        Assert.Equal(3, list.Value.Totals.ExpenseCount);
        Assert.Equal(3000, list.Value.Days!.Sum(d => d.Total));
        Assert.Empty(list.Value.Suggestions!);
        Assert.Equal(BuddyErrorTypes.TripNotFound, (await NewBalances(ctx).SummaryAsync(_trip, _people[1])).Error);
        var balances = await NewBalances(ctx).BalancesAsync(_trip, _people[1]);
        Assert.Single(balances.Value!.People);
        Assert.Equal(BuddyErrorTypes.TripNotFound, (await NewReader(ctx).ListAsync(_trip, Guid.NewGuid(), null, 100)).Error);
    }

    [IntegrationFact]
    public async Task Races_same_id_create_and_same_base_edit_have_one_winner()
    {
        await using (var plain = CreateContext()) await SeedTravellersAsync(plain, 2);
        var id = Guid.NewGuid();
        await using var a = CreateContext();
        await using var b = CreateContext();
        var creates = await Task.WhenAll(
            NewWriter(a).Service.SaveAsync(_trip, id, _owner, NewExpense(900)),
            NewWriter(b).Service.SaveAsync(_trip, id, _owner, NewExpense(1200)));
        Assert.Single(creates, r => r.IsOk && r.Created);
        Assert.Single(creates, r => r.Error == BuddyErrorTypes.ExpenseIdTaken);

        var edits = await Task.WhenAll(
            NewWriter(a).Service.SaveAsync(_trip, id, _owner, NewExpense(1500, baseVersion: 1)),
            NewWriter(b).Service.SaveAsync(_trip, id, _owner, NewExpense(1800, baseVersion: 1)));
        Assert.Single(edits, r => r.IsOk);
        Assert.Single(edits, r => r.Error == BuddyErrorTypes.ExpenseVersionConflict);

        await using var ctx = CreateContext();
        Assert.Equal(2, await ctx.Set<TripExpenseRevision>().CountAsync(r => r.ExpenseId == id));
        var state = await ctx.Set<TripExpenseState>().AsNoTracking().SingleAsync(s => s.TripId == _trip);
        Assert.Equal(1, state.ExpenseCount);
        Assert.Equal(2, state.Version);
    }

    [IntegrationFact]
    public async Task E1_pages_by_day_then_time_with_a_cursor()
    {
        await using (var plain = CreateContext()) await SeedAsync(plain, 7);
        await using var ctx = CreateContext();
        var reader = NewReader(ctx);
        var first = await reader.ListAsync(_trip, _owner, null, 4);
        Assert.Equal(4, first.Value!.Items.Count);
        Assert.NotNull(first.Value.NextCursor);
        Assert.True(BuddyCursors.TryDecodeExpense(first.Value.NextCursor, out var day, out var at, out var id));
        var second = await reader.ListAsync(_trip, _owner, (day, at, id), 4);
        Assert.Equal(3, second.Value!.Items.Count);
        Assert.Null(second.Value.NextCursor);
        Assert.Null(second.Value.Travellers);
        var all = first.Value.Items.Concat(second.Value.Items).ToList();
        Assert.Equal(7, all.Select(i => i.Id).Distinct().Count());
        Assert.Equal(all.OrderByDescending(i => i.DayNumber).ThenByDescending(i => i.CreatedAt).Select(i => i.Id), all.Select(i => i.Id));
        Assert.Equal(7000, first.Value.Days!.Sum(d => d.Total));
    }
}

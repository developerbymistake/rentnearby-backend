using Microsoft.Extensions.Logging;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;

namespace RentNearBy.Tests.TaxiRequests;

public class DueActionLoopTests
{
    private static readonly DateTime T0 = new(2026, 12, 18, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Item_runs_once_when_claimed_twice_in_a_row()
    {
        var h = new Harness();
        var id = h.Store.Add(DueActionKinds.OfferExpire, T0.AddSeconds(-1));
        var secondPassClaimed = -1;
        h.Handle = async (_, _) =>
        {
            // A second pass while the first item is still running: the row is Running, so the claim skips it.
            secondPassClaimed = (await h.Loop.RunOnceAsync(CancellationToken.None)).Claimed;
            return DueOutcome.Done;
        };

        var first = await h.Loop.RunOnceAsync(CancellationToken.None);
        var third = await h.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, first.Claimed);
        Assert.Equal(0, secondPassClaimed);
        Assert.Equal(0, third.Claimed);
        Assert.Equal(1, h.HandlerCalls);
        Assert.Equal(DueActionStates.Done, h.Store.Row(id).State);
        Assert.Single(h.CommittedOutbox);
    }

    [Fact]
    public async Task Runner_whose_lease_was_reaped_and_reclaimed_commits_nothing()
    {
        var h = new Harness();
        var id = h.Store.Add(DueActionKinds.OfferExpire, T0.AddSeconds(-1));
        var oldClaim = Assert.Single(await h.Store.ClaimAsync(50, CancellationToken.None));
        h.Store.Now = oldClaim.LeaseUntil.AddSeconds(1);
        await h.Store.ReapAsync(CancellationToken.None);
        var newClaim = Assert.Single(await h.Store.ClaimAsync(50, CancellationToken.None));

        Assert.Equal(DueItemResult.Stale, await h.Loop.RunItemAsync(oldClaim, CancellationToken.None));
        Assert.Equal(DueItemResult.Done, await h.Loop.RunItemAsync(newClaim, CancellationToken.None));
        Assert.Equal(DueItemResult.Stale, await h.Loop.RunItemAsync(newClaim, CancellationToken.None));

        Assert.Single(h.CommittedOutbox);
        Assert.Equal(DueActionStates.Done, h.Store.Row(id).State);
    }

    [Fact]
    public async Task Lost_claim_detected_at_completion_rolls_back_the_handler_rows()
    {
        var h = new Harness();
        var id = h.Store.Add(DueActionKinds.OfferExpire, T0.AddSeconds(-1));
        var claim = Assert.Single(await h.Store.ClaimAsync(50, CancellationToken.None));
        h.Handle = (_, _) =>
        {
            // The lease expires and another runner takes the row while this handler works.
            h.Store.Now = claim.LeaseUntil.AddSeconds(1);
            h.Store.ReapAsync(CancellationToken.None).GetAwaiter().GetResult();
            h.Store.ClaimAsync(50, CancellationToken.None).GetAwaiter().GetResult();
            return Task.FromResult(DueOutcome.Done);
        };

        Assert.Equal(DueItemResult.Stale, await h.Loop.RunItemAsync(claim, CancellationToken.None));

        Assert.Empty(h.CommittedOutbox);
        Assert.Equal(1, h.RolledBack);
        Assert.Equal(DueActionStates.Running, h.Store.Row(id).State);
    }

    [Fact]
    public async Task Handlers_run_in_their_own_scope_and_write_their_rows_there()
    {
        var h = new Harness();
        for (var i = 0; i < 5; i++) h.Store.Add(DueActionKinds.BookingStart, T0.AddSeconds(-i));

        var pass = await h.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(5, pass.Claimed);
        Assert.Equal(5, h.ScopesBegun);
        Assert.Equal(5, h.Commits);
        Assert.Equal(5, h.CommittedOutbox.Distinct().Count());
        Assert.All(h.Store.Rows, r => Assert.Equal(DueActionStates.Done, r.State));
        Assert.Equal(0, h.Store.ScopesOpenDuringClaim);
    }

    [Fact]
    public async Task Stale_item_is_skipped_not_failed()
    {
        var h = new Harness { Handle = (_, _) => Task.FromResult(DueOutcome.Skipped) };
        var id = h.Store.Add(DueActionKinds.ConfirmExpire, T0.AddSeconds(-1));

        var pass = await h.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal([DueItemResult.Skipped], pass.Results);
        var row = h.Store.Row(id);
        Assert.Equal(DueActionStates.Skipped, row.State);
        Assert.Equal(0, row.Attempts);
        Assert.NotNull(row.DoneAt);
        Assert.Null(row.LastError);
        Assert.DoesNotContain(h.Logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Handler_exception_backs_off_and_parks_after_eight()
    {
        var h = new Harness { Handle = (_, _) => throw new InvalidOperationException("boom") };
        var id = h.Store.Add(DueActionKinds.RequestWaitExpire, T0.AddSeconds(-1));

        for (var attempt = 1; attempt <= DueActionRules.MaxAttempts; attempt++)
        {
            var pass = await h.Loop.RunOnceAsync(CancellationToken.None);
            var row = h.Store.Row(id);
            Assert.Equal(attempt, row.Attempts);
            if (attempt < DueActionRules.MaxAttempts)
            {
                Assert.Equal([DueItemResult.Failed], pass.Results);
                Assert.Equal(DueActionStates.Pending, row.State);
                Assert.Null(row.DoneAt);
                var expected = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, attempt)));
                Assert.Equal(h.Store.Now + expected, row.DueAt);
                Assert.Equal("InvalidOperationException: boom", row.LastError);
                Assert.Equal(DueActionRules.SleepFor(expected.TotalSeconds), pass.Sleep);
                h.Store.Now = row.DueAt;
            }
            else
            {
                Assert.Equal([DueItemResult.Parked], pass.Results);
                Assert.Equal(DueActionStates.Failed, row.State);
                Assert.NotNull(row.DoneAt);
            }
        }

        Assert.Equal(DueActionRules.MaxAttempts, h.HandlerCalls);
        Assert.Equal(0, h.Commits);
        Assert.Single(h.Logger.Entries, e => e.EventId.Id == 7110);
        Assert.Equal(0, (await h.Loop.RunOnceAsync(CancellationToken.None)).Claimed);
    }

    [Fact]
    public async Task Expired_lease_is_reaped()
    {
        var h = new Harness();
        var id = h.Store.Add(DueActionKinds.BookingAutoComplete, T0.AddMinutes(-5));
        var crashed = Assert.Single(await h.Store.ClaimAsync(50, CancellationToken.None));

        h.Store.Now = crashed.LeaseUntil.AddSeconds(-1);
        var early = await h.Loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, early.Claimed);
        Assert.Equal(DueActionStates.Running, h.Store.Row(id).State);

        h.Store.Now = crashed.LeaseUntil.AddSeconds(1);
        var pass = await h.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, pass.Claimed);
        var row = h.Store.Row(id);
        Assert.Equal(DueActionStates.Done, row.State);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(1, h.HandlerCalls);
    }

    [Fact]
    public async Task Lease_reaped_eight_times_is_parked()
    {
        var h = new Harness();
        var id = h.Store.Add(DueActionKinds.BookingStart, T0.AddSeconds(-1));
        for (var i = 0; i < DueActionRules.MaxAttempts; i++)
        {
            var claim = Assert.Single(await h.Store.ClaimAsync(50, CancellationToken.None));
            h.Store.Now = claim.LeaseUntil.AddSeconds(1);
            await h.Store.ReapAsync(CancellationToken.None);
        }

        var row = h.Store.Row(id);
        Assert.Equal(DueActionStates.Failed, row.State);
        Assert.NotNull(row.DoneAt);
        Assert.Equal("Lease expired", row.LastError);
    }

    [Fact]
    public async Task Signal_between_peek_and_wait_is_not_lost()
    {
        var h = new Harness();
        h.Store.OnPeek = () => h.Signal.Wake();

        var pass = await h.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(DueActionRules.MaxSleep, pass.Sleep);
        var woke = await h.Signal.WaitAsync(pass.Sleep, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(woke);
    }

    [Fact]
    public async Task Stale_wake_from_before_the_pass_does_not_cause_a_spurious_pass()
    {
        var h = new Harness();
        h.Signal.Wake();

        var pass = await h.Loop.RunOnceAsync(CancellationToken.None);

        Assert.False(await h.Signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
        Assert.Equal(DueActionRules.MaxSleep, pass.Sleep);
        Assert.True(h.Store.ResetBeforePeek);
    }

    [Theory]
    [InlineData(null, 10.0)]
    [InlineData(3600.0, 10.0)]
    [InlineData(10.0, 10.0)]
    [InlineData(2.5, 2.5)]
    [InlineData(0.0, 0.0)]
    [InlineData(-45.0, 0.0)]
    public void Sleep_is_capped_at_ten_seconds(double? secondsUntilNext, double expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), DueActionRules.SleepFor(secondsUntilNext));
    }

    [Fact]
    public async Task Pass_sleeps_until_the_next_due_row_and_loops_at_once_on_a_full_batch()
    {
        var h = new Harness();
        h.Store.Add(DueActionKinds.OfferExpire, T0.AddSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(4), (await h.Loop.RunOnceAsync(CancellationToken.None)).Sleep);

        h.Store.Add(DueActionKinds.OfferExpire, T0.AddHours(2));
        for (var i = 0; i < DueActionRules.BatchSize; i++) h.Store.Add(DueActionKinds.OfferExpire, T0.AddSeconds(-1));
        var full = await h.Loop.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DueActionRules.BatchSize, full.Claimed);
        Assert.Equal(TimeSpan.Zero, full.Sleep);
    }

    [Fact]
    public async Task Late_backlog_of_full_batches_logs_the_lag_event_once_a_minute()
    {
        var h = new Harness();
        for (var i = 0; i < 3 * DueActionRules.BatchSize; i++) h.Store.Add(DueActionKinds.OfferExpire, T0.AddSeconds(-40));
        var peeksBefore = h.Store.Peeks;

        var first = await h.Loop.RunOnceAsync(CancellationToken.None);
        var second = await h.Loop.RunOnceAsync(CancellationToken.None);
        h.Clock = h.Clock.AddMinutes(2);
        var third = await h.Loop.RunOnceAsync(CancellationToken.None);

        Assert.All(new[] { first, second, third }, p => Assert.Equal(DueActionRules.BatchSize, p.Claimed));
        Assert.Equal(peeksBefore, h.Store.Peeks);
        var lag = h.Logger.Entries.Where(e => e.EventId.Id == 7111).ToList();
        Assert.Equal(2, lag.Count);
        Assert.All(lag, e => Assert.Equal(LogLevel.Warning, e.Level));
    }

    [Fact]
    public async Task Late_partial_batch_logs_the_lag_event()
    {
        var h = new Harness();
        h.Store.Add(DueActionKinds.OfferExpire, T0.AddSeconds(-31));

        var pass = await h.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, pass.Claimed);
        Assert.Single(h.Logger.Entries, e => e.EventId.Id == 7111);
    }

    [Fact]
    public async Task Queue_within_thirty_seconds_logs_no_lag_event()
    {
        var h = new Harness();
        for (var i = 0; i < DueActionRules.BatchSize + 5; i++) h.Store.Add(DueActionKinds.OfferExpire, T0.AddSeconds(-29));

        await h.Loop.RunOnceAsync(CancellationToken.None);
        await h.Loop.RunOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(h.Logger.Entries, e => e.EventId.Id == 7111);
    }

    [Fact]
    public async Task Late_row_seen_only_by_the_peek_logs_the_lag_event()
    {
        // Another worker holds the row (SKIP LOCKED) so this pass claims nothing but the peek still sees it Pending and late.
        var h = new Harness();
        var id = h.Store.Add(DueActionKinds.OfferExpire, T0.AddSeconds(-45));
        h.Store.Lock(id);

        var pass = await h.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, pass.Claimed);
        Assert.Single(h.Logger.Entries, e => e.EventId.Id == 7111);
    }

    [Fact]
    public async Task Unknown_kind_is_parked()
    {
        var h = new Harness();
        var id = h.Store.Add("Mystery", T0.AddSeconds(-1));

        var pass = await h.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal([DueItemResult.Parked], pass.Results);
        var row = h.Store.Row(id);
        Assert.Equal(DueActionStates.Failed, row.State);
        Assert.NotNull(row.DoneAt);
        Assert.Equal("No handler for kind Mystery", row.LastError);
        Assert.Equal(0, h.HandlerCalls);
        Assert.Equal(0, h.Commits);
        Assert.Single(h.Logger.Entries, e => e.EventId.Id == 7110);
    }

    [Fact]
    public async Task Every_final_state_sets_done_at()
    {
        Assert.Contains("\"DoneAt\" = now()", DueActionSql.Complete);
        Assert.Contains("\"DoneAt\" = now()", DueActionSql.Park);
        Assert.Contains("'Failed'", DueActionSql.Park);
        foreach (var sql in new[] { DueActionSql.Fail, DueActionSql.Reap })
        {
            Assert.Contains("\"State\" = CASE WHEN \"Attempts\" + 1 >= 8 THEN 'Failed' ELSE 'Pending' END", sql);
            Assert.Contains("\"DoneAt\" = CASE WHEN \"Attempts\" + 1 >= 8 THEN now() ELSE NULL END", sql);
        }
        foreach (var sql in new[] { DueActionScheduler.CancelSql, DueActionScheduler.CancelForRequestSql })
        {
            Assert.Contains("\"State\" = 'Cancelled', \"DoneAt\" = now()", sql);
            Assert.Contains("\"State\" = 'Pending'", sql);
        }
        Assert.DoesNotContain("DoneAt", DueActionSql.Release);
        Assert.DoesNotContain("DoneAt", DueActionSql.Claim);

        // The in-memory mirror of the same statements: every final state the loop produces carries DoneAt.
        var h = new Harness();
        var done = h.Store.Add(DueActionKinds.OfferExpire, T0.AddSeconds(-4));
        var skipped = h.Store.Add(DueActionKinds.ConfirmExpire, T0.AddSeconds(-3));
        var parked = h.Store.Add("Mystery", T0.AddSeconds(-2));
        h.Handle = (_, item) => Task.FromResult(item.Kind == DueActionKinds.ConfirmExpire ? DueOutcome.Skipped : DueOutcome.Done);
        await h.Loop.RunOnceAsync(CancellationToken.None);
        foreach (var id in new[] { done, skipped, parked })
        {
            var row = h.Store.Row(id);
            Assert.Contains(row.State, DueActionStates.Final);
            Assert.NotNull(row.DoneAt);
        }
    }

    [Fact]
    public void Claim_is_one_statement_with_skip_locked_and_the_exact_index_predicate()
    {
        Assert.Equal("\"State\" = 'Pending' AND \"DueAt\" <= now()", DueActionSql.PendingDuePredicate);
        Assert.Contains("WHERE \"State\" = 'Pending' AND \"DueAt\" <= now()", DueActionSql.Claim);
        Assert.Contains("FOR UPDATE SKIP LOCKED", DueActionSql.Claim);
        Assert.Contains("\"LeaseUntil\" = now() + make_interval(secs => 120)", DueActionSql.Claim);
        Assert.DoesNotContain(";", DueActionSql.Claim);
        Assert.Contains("LEAST(power(2::float8, \"Attempts\" + 1), 300)", DueActionSql.Fail);
        foreach (var sql in new[] { DueActionSql.Complete, DueActionSql.Fail, DueActionSql.Park, DueActionSql.StillClaimed })
            Assert.Contains("\"State\" = 'Running' AND \"LeaseUntil\" = @lease", sql);
        Assert.Contains("ON CONFLICT (\"SubjectId\", \"Kind\", \"RoundNo\") DO NOTHING", DueActionScheduler.InsertAfterDelaySql);
        Assert.Contains("now() + make_interval(secs => @delay)", DueActionScheduler.InsertAfterDelaySql);
        Assert.Contains("ON CONFLICT (\"SubjectId\", \"Kind\", \"RoundNo\") DO NOTHING", DueActionScheduler.InsertAtSql);
    }

    [Fact]
    public async Task Scheduler_rejects_unknown_kinds_negative_delays_and_local_times()
    {
        var scheduler = new DueActionScheduler();
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.AddAsync(null!, "Mystery", Guid.NewGuid(), null, 0, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scheduler.AddAsync(null!, DueActionKinds.OfferExpire, Guid.NewGuid(), null, 0, TimeSpan.FromSeconds(-1)));
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.AddAtAsync(null!, DueActionKinds.BookingStart, Guid.NewGuid(), null, 0, DateTime.Now));
        await scheduler.CancelForRequestAsync(null!, Guid.NewGuid(), []);
    }

    [Fact]
    public async Task Shutdown_releases_claimed_items_that_did_not_start()
    {
        var h = new Harness();
        for (var i = 0; i < 8; i++) h.Store.Add(DueActionKinds.OfferExpire, T0.AddSeconds(-i));
        using var cts = new CancellationTokenSource();
        h.Handle = async (_, _) =>
        {
            await cts.CancelAsync();
            return DueOutcome.Done;
        };

        await h.Loop.RunOnceAsync(cts.Token);

        Assert.All(h.Store.Rows, r => Assert.NotEqual(DueActionStates.Running, r.State));
        Assert.Contains(h.Store.Rows, r => r.State == DueActionStates.Pending && r.Attempts == 0);
    }

    [Fact]
    public async Task After_commit_failure_is_logged_and_the_item_stays_done()
    {
        var h = new Harness { AfterCommitThrows = true };
        var id = h.Store.Add(DueActionKinds.BookingStart, T0.AddSeconds(-1));

        var pass = await h.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal([DueItemResult.Done], pass.Results);
        Assert.Equal(DueActionStates.Done, h.Store.Row(id).State);
        Assert.Contains(h.Logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Outbox_wake_failure_after_commit_is_logged_and_the_item_stays_done()
    {
        var h = new Harness { SignalThrows = true };
        var id = h.Store.Add(DueActionKinds.BookingStart, T0.AddSeconds(-1));

        var pass = await h.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal([DueItemResult.Done], pass.Results);
        Assert.Equal(DueActionStates.Done, h.Store.Row(id).State);
        Assert.Equal(0, h.Store.Row(id).Attempts);
        Assert.Equal(1, h.AfterCommitCalls);
        Assert.Contains(h.Logger.Entries, e => e.Level == LogLevel.Warning && e.Exception?.Message == "relay signal down");
    }

    [Fact]
    public async Task Outbox_is_woken_only_after_the_commit()
    {
        var h = new Harness();
        h.Store.Add(DueActionKinds.BookingStart, T0.AddSeconds(-1));
        h.Store.Add("Mystery", T0.AddSeconds(-1));

        await h.Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, h.Signals);
        Assert.Equal(1, h.Commits);
        Assert.False(h.SignalledBeforeCommit);
    }

    [Fact]
    public async Task Worker_never_throws_out_of_ExecuteAsync()
    {
        var h = new Harness();
        h.Store.ReapThrows = true;
        var beats = new WorkerHeartbeats();
        var worker = new DueActionsWorker(() => h.Loop, h.Signal, h.Logger, beats, TimeSpan.FromMilliseconds(10));

        await worker.StartAsync(CancellationToken.None);
        await h.Store.ReapCalledTwice.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(worker.ExecuteTask!.IsCompleted);
        Assert.NotNull(beats.AgeOf(DueActionsWorker.HeartbeatName));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(TaskStatus.RanToCompletion, worker.ExecuteTask.Status);
        Assert.Contains(h.Logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Worker_wakes_on_signal_instead_of_waiting_the_full_sleep()
    {
        var h = new Harness();
        var worker = new DueActionsWorker(() => h.Loop, h.Signal, h.Logger, null, TimeSpan.FromMilliseconds(10));
        await worker.StartAsync(CancellationToken.None);
        await h.Store.PeekCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        h.Store.Add(DueActionKinds.OfferExpire, T0.AddSeconds(-1));
        h.Signal.Wake();
        await h.FirstCommit.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, h.HandlerCalls);
    }

    // ---- fakes ----

    private sealed class Harness
    {
        public MemoryStore Store { get; }
        public DueActionsSignal Signal { get; } = new();
        public ListLogger Logger { get; } = new();
        public DueActionLoop Loop { get; }
        public Func<ApplicationDbContext, DueActionItem, Task<DueOutcome>> Handle { get; set; } = (_, _) => Task.FromResult(DueOutcome.Done);
        public bool AfterCommitThrows { get; init; }
        public bool SignalThrows { get; init; }
        public bool SignalledBeforeCommit { get; private set; }
        public DateTime Clock { get; set; } = T0;
        private int _handlerCalls, _scopesBegun, _commits, _rolledBack, _signals, _afterCommitCalls;
        public int Signals => _signals;
        public int AfterCommitCalls => _afterCommitCalls;
        public int HandlerCalls => _handlerCalls;
        public int ScopesBegun => _scopesBegun;
        public int Commits => _commits;
        public int RolledBack => _rolledBack;
        public System.Collections.Concurrent.ConcurrentBag<Guid> CommittedOutbox { get; } = [];
        public TaskCompletionSource FirstCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OpenScopes;

        public Harness()
        {
            Store = new MemoryStore(this);
            Loop = new DueActionLoop(Store, new ScopeFactory(this), Signal, Logger, new TaxiRequestMetrics(), () => Clock);
        }

        private sealed class ScopeFactory(Harness h) : IDueActionItemScopeFactory
        {
            public Task<IDueActionItemScope> BeginAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref h._scopesBegun);
                Interlocked.Increment(ref h.OpenScopes);
                return Task.FromResult<IDueActionItemScope>(new Scope(h));
            }
        }

        private sealed class Scope(Harness h) : IDueActionItemScope
        {
            private readonly List<Guid> _pendingOutbox = [];
            private (DueClaim Claim, DueOutcome Outcome)? _completion;
            private bool _committed;

            public ApplicationDbContext Db => null!;

            public IDueActionHandler? HandlerFor(string kind)
                => DueActionKinds.All.Contains(kind) ? new Handler(h, kind, _pendingOutbox) : null;

            public Task<bool> IsStillClaimedAsync(DueClaim claim, CancellationToken ct) => Task.FromResult(h.Store.IsClaimed(claim));

            public Task<bool> CompleteAsync(DueClaim claim, DueOutcome outcome, CancellationToken ct)
            {
                if (!h.Store.IsClaimed(claim)) return Task.FromResult(false);
                _completion = (claim, outcome);
                return Task.FromResult(true);
            }

            public Task CommitAsync(CancellationToken ct)
            {
                lock (h.Store)
                {
                    if (_completion is { } c && !h.Store.Complete(c.Claim, c.Outcome)) throw new InvalidOperationException("Lost the row at commit");
                    foreach (var row in _pendingOutbox) h.CommittedOutbox.Add(row);
                }
                _committed = true;
                Interlocked.Increment(ref h._commits);
                h.FirstCommit.TrySetResult();
                return Task.CompletedTask;
            }

            public void SignalCommitted()
            {
                if (!_committed) h.SignalledBeforeCommit = true;
                Interlocked.Increment(ref h._signals);
                if (h.SignalThrows) throw new InvalidOperationException("relay signal down");
            }

            public ValueTask DisposeAsync()
            {
                if (!_committed) Interlocked.Increment(ref h._rolledBack);
                Interlocked.Decrement(ref h.OpenScopes);
                return ValueTask.CompletedTask;
            }
        }

        private sealed class Handler(Harness h, string kind, List<Guid> outbox) : IDueActionHandler, IDueActionAfterCommit
        {
            public string Kind => kind;

            public async Task<DueOutcome> HandleAsync(ApplicationDbContext db, DueActionItem item, CancellationToken ct)
            {
                Interlocked.Increment(ref h._handlerCalls);
                var outcome = await h.Handle(db, item);
                outbox.Add(item.Id);
                return outcome;
            }

            public Task AfterCommitAsync(DueActionItem item, DueOutcome outcome, CancellationToken ct)
            {
                Interlocked.Increment(ref h._afterCommitCalls);
                return h.AfterCommitThrows ? throw new InvalidOperationException("signal down") : Task.CompletedTask;
            }
        }
    }

    public sealed class Row
    {
        public Guid Id { get; init; }
        public string Kind { get; init; } = "";
        public DateTime DueAt { get; set; }
        public string State { get; set; } = DueActionStates.Pending;
        public DateTime? LeaseUntil { get; set; }
        public short Attempts { get; set; }
        public string? LastError { get; set; }
        public DateTime? DoneAt { get; set; }
    }

    // Mirrors the SQL of DueActionSql: one now() per statement, LeaseUntil as the claim token.
    private sealed class MemoryStore(Harness h) : IDueActionStore
    {
        public DateTime Now { get; set; } = T0;
        public List<Row> Rows { get; } = [];
        public Action? OnPeek { get; set; }
        public bool ReapThrows { get; set; }
        public int Peeks => _peeks;
        private readonly HashSet<Guid> _locked = [];
        private int _peeks;

        // Failure n waits 2^n seconds, capped at 5 min, and the eighth failure parks: the C# mirror of DueActionSql.Fail/Reap,
        // whose real arithmetic is pinned by the SQL text tests and DueActionStoreIntegrationTests.
        private static TimeSpan Backoff(int attemptsAfterFailure)
            => TimeSpan.FromSeconds(Math.Min(DueActionRules.MaxBackoffSeconds, Math.Pow(2, Math.Clamp(attemptsAfterFailure, 1, 30))));

        private static bool ShouldPark(int attemptsAfterFailure) => attemptsAfterFailure >= DueActionRules.MaxAttempts;

        // Row-locked by another transaction: SKIP LOCKED passes over it, the plain peek still sees it.
        public void Lock(Guid id)
        {
            lock (this) _locked.Add(id);
        }
        public int ScopesOpenDuringClaim { get; private set; }
        public bool ResetBeforePeek { get; private set; }
        public TaskCompletionSource ReapCalledTwice { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PeekCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reapCalls;
        private long _statement;

        public Guid Add(string kind, DateTime dueAt)
        {
            var id = Guid.NewGuid();
            lock (this) Rows.Add(new Row { Id = id, Kind = kind, DueAt = dueAt });
            return id;
        }

        public Row Row(Guid id)
        {
            lock (this) return Rows.Single(r => r.Id == id);
        }

        // Each statement sees its own now(), a microsecond apart, like separate auto-commit statements.
        private DateTime StatementNow() => Now.AddTicks(10 * Interlocked.Increment(ref _statement));

        public bool IsClaimed(DueClaim c)
        {
            lock (this) return Rows.Any(r => r.Id == c.Item.Id && r.State == DueActionStates.Running && r.LeaseUntil == c.LeaseUntil);
        }

        public bool Complete(DueClaim c, DueOutcome outcome)
        {
            lock (this)
            {
                var row = Rows.SingleOrDefault(r => r.Id == c.Item.Id && r.State == DueActionStates.Running && r.LeaseUntil == c.LeaseUntil);
                if (row is null) return false;
                row.State = outcome == DueOutcome.Done ? DueActionStates.Done : DueActionStates.Skipped;
                row.DoneAt = StatementNow();
                row.LeaseUntil = null;
                return true;
            }
        }

        public Task<DueReapResult> ReapAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _reapCalls) >= 2) ReapCalledTwice.TrySetResult();
            if (ReapThrows) throw new TimeoutException("database down");
            lock (this)
            {
                var now = StatementNow();
                int reaped = 0, parked = 0;
                foreach (var r in Rows.Where(r => r.State == DueActionStates.Running && r.LeaseUntil < now))
                {
                    reaped++;
                    r.Attempts++;
                    r.LeaseUntil = null;
                    if (ShouldPark(r.Attempts))
                    {
                        parked++;
                        r.State = DueActionStates.Failed;
                        r.DoneAt = now;
                        r.LastError = "Lease expired";
                    }
                    else r.State = DueActionStates.Pending;
                }
                return Task.FromResult(new DueReapResult(reaped, parked));
            }
        }

        public Task<IReadOnlyList<DueClaim>> ClaimAsync(int batchSize, CancellationToken ct)
        {
            ScopesOpenDuringClaim = Math.Max(ScopesOpenDuringClaim, Volatile.Read(ref h.OpenScopes));
            lock (this)
            {
                var now = StatementNow();
                var lease = now.AddSeconds(DueActionRules.LeaseSeconds);
                var claimed = Rows.Where(r => r.State == DueActionStates.Pending && r.DueAt <= now && !_locked.Contains(r.Id))
                    .OrderBy(r => r.DueAt).ThenBy(r => r.Id).Take(batchSize).ToList();
                foreach (var r in claimed)
                {
                    r.State = DueActionStates.Running;
                    r.LeaseUntil = lease;
                }
                return Task.FromResult<IReadOnlyList<DueClaim>>(claimed
                    .Select(r => new DueClaim(new DueActionItem(r.Id, r.Kind, Guid.NewGuid(), 0, null, r.DueAt, r.Attempts), lease, (now - r.DueAt).TotalSeconds))
                    .ToList());
            }
        }

        public Task<double?> PeekNextDueSecondsAsync(CancellationToken ct)
        {
            ResetBeforePeek = !h.Signal.WaitAsync(TimeSpan.Zero, CancellationToken.None).Result;
            Interlocked.Increment(ref _peeks);
            PeekCalled.TrySetResult();
            OnPeek?.Invoke();
            lock (this)
            {
                var pending = Rows.Where(r => r.State == DueActionStates.Pending).ToList();
                return Task.FromResult<double?>(pending.Count == 0 ? null : (pending.Min(r => r.DueAt) - Now).TotalSeconds);
            }
        }

        public Task<DueFailResult> FailAsync(DueClaim claim, string error, CancellationToken ct)
        {
            lock (this)
            {
                var r = Rows.SingleOrDefault(x => x.Id == claim.Item.Id && x.State == DueActionStates.Running && x.LeaseUntil == claim.LeaseUntil);
                if (r is null) return Task.FromResult(new DueFailResult(false, false));
                r.Attempts++;
                r.LastError = error;
                r.LeaseUntil = null;
                if (ShouldPark(r.Attempts))
                {
                    r.State = DueActionStates.Failed;
                    r.DoneAt = StatementNow();
                    return Task.FromResult(new DueFailResult(true, true));
                }
                r.State = DueActionStates.Pending;
                r.DueAt = Now + Backoff(r.Attempts);
                return Task.FromResult(new DueFailResult(true, false));
            }
        }

        public Task<bool> ParkAsync(DueClaim claim, string error, CancellationToken ct)
        {
            lock (this)
            {
                var r = Rows.SingleOrDefault(x => x.Id == claim.Item.Id && x.State == DueActionStates.Running && x.LeaseUntil == claim.LeaseUntil);
                if (r is null) return Task.FromResult(false);
                r.Attempts++;
                r.State = DueActionStates.Failed;
                r.DoneAt = StatementNow();
                r.LastError = error;
                r.LeaseUntil = null;
                return Task.FromResult(true);
            }
        }

        public Task<int> ReleaseAsync(IReadOnlyList<DueClaim> claims, CancellationToken ct)
        {
            lock (this)
            {
                var n = 0;
                foreach (var c in claims)
                {
                    var r = Rows.SingleOrDefault(x => x.Id == c.Item.Id && x.State == DueActionStates.Running && x.LeaseUntil == c.LeaseUntil);
                    if (r is null) continue;
                    r.State = DueActionStates.Pending;
                    r.LeaseUntil = null;
                    n++;
                }
                return Task.FromResult(n);
            }
        }
    }

    public sealed class ListLogger : ILogger
    {
        public sealed record Entry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

        private readonly System.Collections.Concurrent.ConcurrentQueue<Entry> _entries = new();
        public IReadOnlyList<Entry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue(new Entry(logLevel, eventId, formatter(state, exception), exception));
    }
}

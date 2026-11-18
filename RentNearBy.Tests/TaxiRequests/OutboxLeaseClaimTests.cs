using System.Text.RegularExpressions;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.TaxiRequests;

public class OutboxLeaseClaimTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 6, 0, 0, DateTimeKind.Utc);

    private sealed class StoredRow
    {
        public required OutboxRow Row { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime NextAttemptAt { get; set; }
        public DateTime? PublishedAt { get; set; }
        public string? LastError { get; set; }
    }

    // In-memory twin of PostgresOutboxLeaseStore: the claim leases due rows with the same predicate and lease length.
    private sealed class MemoryStore(Func<DateTime> clock) : IOutboxLeaseStore
    {
        public List<StoredRow> Rows { get; } = [];
        public bool TransactionOpen { get; set; }
        public List<string> Calls { get; } = [];

        public bool HasOpenTransaction => TransactionOpen;

        public StoredRow Add(long id, short attempts = 0)
        {
            var row = new StoredRow
            {
                Row = new OutboxRow { Id = id, Key = $"k{id}", Exchange = "rnb.push", RoutingKey = "send", Payload = "{}", Attempts = attempts },
                CreatedAt = clock(), NextAttemptAt = clock(),
            };
            Rows.Add(row);
            return row;
        }

        public Task<IReadOnlyList<OutboxRow>> ClaimAsync(CancellationToken ct)
        {
            Calls.Add("claim");
            var now = clock();
            var due = Rows.Where(r => OutboxRelayLogic.IsClaimable(r.PublishedAt, r.NextAttemptAt, r.CreatedAt, now))
                .OrderBy(r => r.NextAttemptAt).ThenBy(r => r.Row.Id).Take(OutboxRelayLogic.BatchSize).ToList();
            foreach (var r in due) r.NextAttemptAt = OutboxRelayLogic.LeaseUntil(now);
            return Task.FromResult<IReadOnlyList<OutboxRow>>(due.Select(r => r.Row).ToList());
        }

        public Task MarkPublishedAsync(IReadOnlyList<long> ids, CancellationToken ct)
        {
            Calls.Add("published");
            foreach (var r in Rows.Where(r => ids.Contains(r.Row.Id))) { r.PublishedAt = clock(); r.LastError = null; }
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(IReadOnlyList<OutboxFailure> failures, CancellationToken ct)
        {
            Calls.Add("failed");
            foreach (var f in failures)
            {
                var r = Rows.Single(x => x.Row.Id == f.Id);
                if (r.PublishedAt is not null) continue;
                r.Row.Attempts = f.Attempts;
                r.NextAttemptAt = f.NextAttemptAt;
                r.LastError = f.Error;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class Publisher(Func<ConfirmedMessage, PublishResult> decide, Action? onPublish = null) : IConfirmedPublisher
    {
        public List<ConfirmedMessage> Sent { get; } = [];

        public Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<ConfirmedMessage> messages, CancellationToken ct = default)
        {
            onPublish?.Invoke();
            Sent.AddRange(messages);
            return Task.FromResult<IReadOnlyList<PublishResult>>(messages.Select(decide).ToList());
        }
    }

    private sealed class ThrowingPublisher(Exception ex) : IConfirmedPublisher
    {
        public Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<ConfirmedMessage> messages, CancellationToken ct = default)
            => throw ex;
    }

    private static int Count(string text, string part) => Regex.Matches(text, Regex.Escape(part)).Count;

    [Fact]
    public void Claim_sql_repeats_the_pending_predicate_and_sets_a_lease()
    {
        var sql = OutboxRelayLogic.ClaimSql;

        Assert.Equal(2, Count(sql, OutboxRelayLogic.PendingPredicate));
        Assert.Contains("FOR UPDATE SKIP LOCKED", sql);
        Assert.Contains("""LIMIT {1}""", sql);
        Assert.Contains("""SET "NextAttemptAt" = now() + make_interval(secs => {2})""", sql);
        Assert.Contains("""RETURNING o."Id", o."Key", o."Exchange", o."RoutingKey", o."Payload", o."Attempts" """.TrimEnd(), sql);
        Assert.StartsWith("WITH due AS", sql.TrimStart());
        Assert.DoesNotContain("BEGIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(";", sql);
        Assert.Equal(new object[] { 72, 500, 60 }, OutboxRelayLogic.ClaimParameters);
        Assert.Equal(60, OutboxRelayLogic.LeaseSeconds);
        Assert.Equal(T0.AddSeconds(60), OutboxRelayLogic.LeaseUntil(T0));
        Assert.Equal("""{0} {1} {2}""", string.Join(' ', Regex.Matches(sql, @"\{\d\}").Select(m => m.Value).Distinct().Order()));
    }

    [Fact]
    public async Task Publish_runs_without_an_open_transaction()
    {
        var store = new MemoryStore(() => T0);
        store.Add(1);
        bool? openAtPublish = null;
        var pub = new Publisher(_ => new PublishResult(true), () =>
        {
            openAtPublish = store.HasOpenTransaction;
            store.Calls.Add("publish");
        });

        var claimed = await OutboxRelayLogic.RelayOnceAsync(store, pub, () => T0, CancellationToken.None);

        Assert.Equal(1, claimed);
        Assert.False(openAtPublish);
        Assert.Equal(["claim", "publish", "published"], store.Calls);
        Assert.NotNull(store.Rows[0].PublishedAt);

        var guarded = new MemoryStore(() => T0) { TransactionOpen = true };
        guarded.Add(2);
        var never = new Publisher(_ => new PublishResult(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => OutboxRelayLogic.RelayOnceAsync(guarded, never, () => T0, CancellationToken.None));
        Assert.Empty(never.Sent);
        Assert.Empty(guarded.Calls);
    }

    [Fact]
    public async Task Crash_after_claim_makes_the_row_due_after_the_lease()
    {
        var now = T0;
        var store = new MemoryStore(() => now);
        var row = store.Add(1);
        var crashing = new ThrowingPublisher(new OperationCanceledException("process stopped"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => OutboxRelayLogic.RelayOnceAsync(store, crashing, () => now, CancellationToken.None));
        Assert.Equal(["claim"], store.Calls);
        Assert.Equal(T0.AddSeconds(OutboxRelayLogic.LeaseSeconds), row.NextAttemptAt);
        Assert.Equal(0, row.Row.Attempts);
        Assert.Null(row.PublishedAt);

        now = T0.AddSeconds(OutboxRelayLogic.LeaseSeconds - 1);
        var early = new Publisher(_ => new PublishResult(true));
        Assert.Equal(0, await OutboxRelayLogic.RelayOnceAsync(store, early, () => now, CancellationToken.None));
        Assert.Empty(early.Sent);

        now = T0.AddSeconds(OutboxRelayLogic.LeaseSeconds);
        var later = new Publisher(_ => new PublishResult(true));
        Assert.Equal(1, await OutboxRelayLogic.RelayOnceAsync(store, later, () => now, CancellationToken.None));
        Assert.Equal(["k1"], later.Sent.Select(m => m.MessageId));
        Assert.NotNull(row.PublishedAt);
    }

    [Fact]
    public async Task Failure_overrides_the_lease_with_backoff()
    {
        var store = new MemoryStore(() => T0);
        var row = store.Add(1, attempts: 2);
        var nack = new Publisher(_ => new PublishResult(false, "nack"));

        await OutboxRelayLogic.RelayOnceAsync(store, nack, () => T0, CancellationToken.None);

        Assert.Equal(["claim", "failed"], store.Calls);
        Assert.Equal(3, row.Row.Attempts);
        Assert.Equal(T0 + OutboxRelayLogic.Backoff(3), row.NextAttemptAt);
        Assert.NotEqual(OutboxRelayLogic.LeaseUntil(T0), row.NextAttemptAt);
        Assert.Equal("nack", row.LastError);
        Assert.Null(row.PublishedAt);
    }

    [Fact]
    public async Task Unavailable_broker_does_not_count_an_attempt()
    {
        var store = new MemoryStore(() => T0);
        var reported = store.Add(1, attempts: 4);
        var down = new Publisher(_ => new PublishResult(false, OutboxRelayLogic.UnavailablePrefix + "connection refused"));
        await OutboxRelayLogic.RelayOnceAsync(store, down, () => T0, CancellationToken.None);

        Assert.Equal(4, reported.Row.Attempts);
        Assert.Equal(T0.AddSeconds(OutboxRelayLogic.UnavailableRetrySeconds), reported.NextAttemptAt);
        Assert.True(OutboxRelayLogic.IsUnavailable(reported.LastError));

        var thrownStore = new MemoryStore(() => T0);
        var thrown = thrownStore.Add(2, attempts: 1);
        await OutboxRelayLogic.RelayOnceAsync(thrownStore, new ThrowingPublisher(new InvalidOperationException("socket closed")), () => T0, CancellationToken.None);

        Assert.Equal(1, thrown.Row.Attempts);
        Assert.Equal(T0.AddSeconds(OutboxRelayLogic.UnavailableRetrySeconds), thrown.NextAttemptAt);
        Assert.Equal(OutboxRelayLogic.UnavailablePrefix + "socket closed", thrown.LastError);
        Assert.Null(thrown.PublishedAt);
    }
}

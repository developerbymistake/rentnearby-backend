using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.TaxiRequests;

// Needs PostgreSQL (RENTNEARBY_TEST_DB); skipped without it.
[Trait("Category", "Integration")]
public class DueActionStoreIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private sealed class DoneHandler(string kind) : IDueActionHandler
    {
        public string Kind => kind;
        public Task<DueOutcome> HandleAsync(ApplicationDbContext db, DueActionItem item, CancellationToken ct) => Task.FromResult(DueOutcome.Done);
    }

    // Adds an outbox row in the item's transaction; Before runs after the add, before the Done mark.
    private sealed class OutboxHandler(string kind, Func<Task>? before = null) : IDueActionHandler
    {
        public string Kind => kind;

        public async Task<DueOutcome> HandleAsync(ApplicationDbContext db, DueActionItem item, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            db.OutboxMessages.Add(new OutboxMessage
            {
                Key = "due:" + item.Id.ToString("N"), Exchange = OutboxTargets.PushExchange, RoutingKey = OutboxTargets.PushRoutingKey,
                Payload = "{}", CreatedAt = now, NextAttemptAt = now,
            });
            if (before is not null) await before();
            return DueOutcome.Done;
        }
    }

    private sealed class ThrowingHandler(string kind) : IDueActionHandler
    {
        public string Kind => kind;
        public Task<DueOutcome> HandleAsync(ApplicationDbContext db, DueActionItem item, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    private IServiceScopeFactory Scopes(Func<string, IDueActionHandler>? handler = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateContext());
        foreach (var kind in DueActionKinds.All) services.AddScoped<IDueActionHandler>(_ => handler?.Invoke(kind) ?? new DoneHandler(kind));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private async Task<bool> OutboxRowExistsAsync(Guid dueActionId)
    {
        await using var ctx = CreateContext();
        var key = "due:" + dueActionId.ToString("N");
        return await ctx.OutboxMessages.AsNoTracking().AnyAsync(m => m.Key == key);
    }

    private DueActionLoop Loop(IServiceScopeFactory scopes)
        => new(new PostgresDueActionStore(scopes), new DueActionItemScopeFactory(scopes), new DueActionsSignal(), NullLogger.Instance);

    private async Task ClearAsync()
    {
        await using var ctx = CreateContext();
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM \"DueActions\"");
    }

    private async Task<List<Guid>> SeedAsync(int count, DateTime dueAtUtc)
    {
        var scheduler = new DueActionScheduler();
        var subjects = new List<Guid>(count);
        await using var ctx = CreateContext();
        await using var tx = await ctx.Database.BeginTransactionAsync();
        for (var i = 0; i < count; i++)
        {
            var subject = Guid.NewGuid();
            subjects.Add(subject);
            await scheduler.AddAtAsync(ctx, DueActionKinds.OfferExpire, subject, null, 0, dueAtUtc);
        }
        await tx.CommitAsync();
        return subjects;
    }

    private async Task<List<DueAction>> RowsAsync()
    {
        await using var ctx = CreateContext();
        return await ctx.Set<DueAction>().AsNoTracking().ToListAsync();
    }

    [IntegrationFact]
    public async Task Claim_uses_skip_locked_under_two_workers()
    {
        await ClearAsync();
        await SeedAsync(400, DateTime.UtcNow.AddMinutes(-1));
        var scopes = Scopes();
        var a = new PostgresDueActionStore(scopes);
        var b = new PostgresDueActionStore(scopes);

        var claimed = new List<Guid>();
        while (true)
        {
            var results = await Task.WhenAll(a.ClaimAsync(25, CancellationToken.None), b.ClaimAsync(25, CancellationToken.None));
            if (results.All(r => r.Count == 0)) break;
            claimed.AddRange(results.SelectMany(r => r).Select(c => c.Item.Id));
        }

        Assert.Equal(400, claimed.Count);
        Assert.Equal(400, claimed.Distinct().Count());
        Assert.All(await RowsAsync(), r => Assert.Equal(DueActionStates.Running, r.State));
    }

    [IntegrationFact]
    public async Task Five_thousand_rows_with_the_same_due_time_drain()
    {
        await ClearAsync();
        await SeedAsync(5000, DateTime.UtcNow.AddSeconds(-5));
        var loop = Loop(Scopes());

        var passes = 0;
        while ((await loop.RunOnceAsync(CancellationToken.None)).Claimed > 0 && passes < 200) passes++;

        var rows = await RowsAsync();
        Assert.Equal(5000, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(DueActionStates.Done, r.State);
            Assert.NotNull(r.DoneAt);
            Assert.Null(r.LeaseUntil);
        });
        Assert.Equal(100, passes);
    }

    [IntegrationFact]
    public async Task Cancel_racing_the_claim_is_skipped()
    {
        await ClearAsync();
        var subject = (await SeedAsync(1, DateTime.UtcNow.AddSeconds(-1)))[0];
        var scopes = Scopes();
        var store = new PostgresDueActionStore(scopes);

        await using (var ctx = CreateContext())
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            await new DueActionScheduler().CancelAsync(ctx, DueActionKinds.OfferExpire, subject);
            Assert.Empty(await store.ClaimAsync(50, CancellationToken.None));
            await tx.CommitAsync();
        }
        Assert.Empty(await store.ClaimAsync(50, CancellationToken.None));

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(DueActionStates.Cancelled, row.State);
        Assert.NotNull(row.DoneAt);

        // Once Running, a cancel no longer touches the row: the handler re-checks the domain state instead.
        var other = (await SeedAsync(1, DateTime.UtcNow.AddSeconds(-1)))[0];
        var claim = Assert.Single(await store.ClaimAsync(50, CancellationToken.None));
        await using (var ctx = CreateContext())
            await new DueActionScheduler().CancelAsync(ctx, DueActionKinds.OfferExpire, other);
        Assert.Equal(DueItemResult.Done, await Loop(scopes).RunItemAsync(claim, CancellationToken.None));
    }

    [IntegrationFact]
    public async Task Restart_mid_batch_recovers()
    {
        await ClearAsync();
        await SeedAsync(10, DateTime.UtcNow.AddSeconds(-1));
        var scopes = Scopes();
        var crashed = await new PostgresDueActionStore(scopes).ClaimAsync(50, CancellationToken.None);
        Assert.Equal(10, crashed.Count);

        await using (var ctx = CreateContext())
            await ctx.Database.ExecuteSqlRawAsync("UPDATE \"DueActions\" SET \"LeaseUntil\" = now() - interval '1 second' WHERE \"State\" = 'Running'");

        var pass = await Loop(scopes).RunOnceAsync(CancellationToken.None);

        Assert.Equal(10, pass.Claimed);
        Assert.All(await RowsAsync(), r =>
        {
            Assert.Equal(DueActionStates.Done, r.State);
            Assert.Equal(1, r.Attempts);
        });
        // The crashed runner's claims can no longer complete anything.
        Assert.Equal(DueItemResult.Stale, await Loop(scopes).RunItemAsync(crashed[0], CancellationToken.None));
    }

    [IntegrationFact]
    public async Task Failure_backs_off_on_the_database_clock_and_add_is_idempotent()
    {
        await ClearAsync();
        var subject = (await SeedAsync(1, DateTime.UtcNow.AddSeconds(-1)))[0];
        await using (var ctx = CreateContext())
            await new DueActionScheduler().AddAsync(ctx, DueActionKinds.OfferExpire, subject, null, 0, TimeSpan.FromMinutes(5));
        var store = new PostgresDueActionStore(Scopes());
        var claim = Assert.Single(await store.ClaimAsync(50, CancellationToken.None));

        var result = await store.FailAsync(claim, "boom", CancellationToken.None);

        Assert.True(result.Applied);
        Assert.False(result.Parked);
        var row = Assert.Single(await RowsAsync());
        Assert.Equal(DueActionStates.Pending, row.State);
        Assert.Null(row.DoneAt);
        Assert.Equal(1, row.Attempts);
        Assert.InRange(await store.PeekNextDueSecondsAsync(CancellationToken.None) ?? -1, 0.5, 2.5);
    }

    [IntegrationFact]
    public async Task Handler_outbox_row_commits_with_done()
    {
        await ClearAsync();
        await SeedAsync(1, DateTime.UtcNow.AddSeconds(-1));
        var scopes = Scopes(kind => new OutboxHandler(kind));
        var claim = Assert.Single(await new PostgresDueActionStore(scopes).ClaimAsync(50, CancellationToken.None));

        Assert.Equal(DueItemResult.Done, await Loop(scopes).RunItemAsync(claim, CancellationToken.None));

        Assert.True(await OutboxRowExistsAsync(claim.Item.Id));
        var row = Assert.Single(await RowsAsync());
        Assert.Equal(DueActionStates.Done, row.State);
        Assert.NotNull(row.DoneAt);
        Assert.Null(row.LeaseUntil);
    }

    [IntegrationFact]
    public async Task Handler_outbox_row_rolls_back_when_the_claim_is_lost()
    {
        await ClearAsync();
        await SeedAsync(1, DateTime.UtcNow.AddSeconds(-1));
        IServiceScopeFactory? scopes = null;
        DueClaim? newer = null;
        scopes = Scopes(kind => new OutboxHandler(kind, async () =>
        {
            // The lease expires and another runner reaps and re-claims the row while this handler works.
            await using (var ctx = CreateContext())
                await ctx.Database.ExecuteSqlRawAsync("UPDATE \"DueActions\" SET \"LeaseUntil\" = now() - interval '1 second' WHERE \"State\" = 'Running'");
            var other = new PostgresDueActionStore(scopes!);
            await other.ReapAsync(CancellationToken.None);
            newer = Assert.Single(await other.ClaimAsync(50, CancellationToken.None));
        }));
        var claim = Assert.Single(await new PostgresDueActionStore(scopes).ClaimAsync(50, CancellationToken.None));

        Assert.Equal(DueItemResult.Stale, await Loop(scopes).RunItemAsync(claim, CancellationToken.None));

        Assert.False(await OutboxRowExistsAsync(claim.Item.Id));
        var row = Assert.Single(await RowsAsync());
        Assert.Equal(DueActionStates.Running, row.State);
        Assert.Equal(newer!.LeaseUntil, row.LeaseUntil);
        Assert.Equal(1, row.Attempts);
    }

    [IntegrationFact]
    public async Task Real_sql_backs_off_two_to_the_n_and_parks_on_the_eighth_failure()
    {
        await ClearAsync();
        await SeedAsync(1, DateTime.UtcNow.AddSeconds(-1));
        var scopes = Scopes(kind => new ThrowingHandler(kind));
        var store = new PostgresDueActionStore(scopes);
        var loop = Loop(scopes);

        for (var attempt = 1; attempt <= DueActionRules.MaxAttempts; attempt++)
        {
            var claim = Assert.Single(await store.ClaimAsync(50, CancellationToken.None));
            var result = await loop.RunItemAsync(claim, CancellationToken.None);
            var row = Assert.Single(await RowsAsync());
            Assert.Equal(attempt, row.Attempts);
            Assert.Equal("InvalidOperationException: boom", row.LastError);
            if (attempt < DueActionRules.MaxAttempts)
            {
                Assert.Equal(DueItemResult.Failed, result);
                Assert.Equal(DueActionStates.Pending, row.State);
                Assert.Null(row.DoneAt);
                var expected = Math.Min(DueActionRules.MaxBackoffSeconds, Math.Pow(2, attempt));
                Assert.InRange(await store.PeekNextDueSecondsAsync(CancellationToken.None) ?? double.NaN, expected - 2, expected + 0.5);
                await using var ctx = CreateContext();
                await ctx.Database.ExecuteSqlRawAsync("UPDATE \"DueActions\" SET \"DueAt\" = now() - interval '1 second'");
            }
            else
            {
                Assert.Equal(DueItemResult.Parked, result);
                Assert.Equal(DueActionStates.Failed, row.State);
                Assert.NotNull(row.DoneAt);
                Assert.Null(row.LeaseUntil);
            }
        }
        Assert.Empty(await store.ClaimAsync(50, CancellationToken.None));
    }
}

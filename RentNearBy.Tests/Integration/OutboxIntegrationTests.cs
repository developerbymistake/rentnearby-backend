using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;

namespace RentNearBy.Tests.Integration;

// Written for PostgreSQL (RENTNEARBY_TEST_DB); skipped without it. The relay is driven with a fake confirmed publisher.
public class OutboxIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private sealed class ScriptedPublisher(Func<ConfirmedMessage, bool> confirm) : IConfirmedPublisher
    {
        public List<ConfirmedMessage> Sent { get; } = [];

        public Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<ConfirmedMessage> messages, CancellationToken ct = default)
        {
            Sent.AddRange(messages);
            return Task.FromResult<IReadOnlyList<PublishResult>>(messages.Select(m => confirm(m) ? new PublishResult(true) : new PublishResult(false, "nack")).ToList());
        }
    }

    private OutboxRelayService Relay(IConfirmedPublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateContext());
        return new OutboxRelayService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), publisher, new OutboxSignal(),
            NullLogger<OutboxRelayService>.Instance);
    }

    private async Task<string> AddRowAsync(int attempts = 0, DateTime? due = null)
    {
        var key = "test:" + Guid.NewGuid().ToString("N");
        await using var ctx = CreateContext();
        var now = DateTime.UtcNow;
        ctx.OutboxMessages.Add(new OutboxMessage
        {
            Key = key, Exchange = "rnb.push", RoutingKey = "send", Payload = "{}", CreatedAt = now,
            NextAttemptAt = due ?? now.AddSeconds(-1), Attempts = (short)attempts,
        });
        await ctx.SaveChangesAsync();
        return key;
    }

    [IntegrationFact]
    public async Task Confirmed_rows_are_marked_published_and_not_sent_twice()
    {
        var key = await AddRowAsync();
        var publisher = new ScriptedPublisher(_ => true);
        var relay = Relay(publisher);

        await relay.RelayBatchAsync(CancellationToken.None);
        await relay.RelayBatchAsync(CancellationToken.None);

        Assert.Single(publisher.Sent, m => m.MessageId == key);
        await using var ctx = CreateContext();
        Assert.NotNull((await ctx.OutboxMessages.AsNoTracking().SingleAsync(o => o.Key == key)).PublishedAt);
    }

    [IntegrationFact]
    public async Task A_nack_keeps_the_row_pending_with_a_backoff_and_a_crash_before_publish_loses_nothing()
    {
        var key = await AddRowAsync();
        var failing = new ScriptedPublisher(_ => false);
        await Relay(failing).RelayBatchAsync(CancellationToken.None);

        await using (var ctx = CreateContext())
        {
            var row = await ctx.OutboxMessages.AsNoTracking().SingleAsync(o => o.Key == key);
            Assert.Null(row.PublishedAt);
            Assert.Equal(1, row.Attempts);
            Assert.True(row.NextAttemptAt > DateTime.UtcNow);
            Assert.Equal("nack", row.LastError);
        }

        // The row is still there for the next relay (a restart): make it due and publish with a healthy broker.
        await using (var ctx = CreateContext())
            await ctx.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "OutboxMessages" SET "NextAttemptAt" = now() WHERE "Key" = {key}""");
        var healthy = new ScriptedPublisher(_ => true);
        await Relay(healthy).RelayBatchAsync(CancellationToken.None);
        Assert.Contains(healthy.Sent, m => m.MessageId == key);
    }

    [IntegrationFact]
    public async Task Rows_older_than_the_give_up_age_are_left_alone_but_many_attempts_are_not_a_reason()
    {
        var key = await AddRowAsync();
        var many = await AddRowAsync(attempts: 400);
        await using (var ctx = CreateContext())
            await ctx.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "OutboxMessages" SET "CreatedAt" = now() - interval '73 hours' WHERE "Key" = {key}""");
        var publisher = new ScriptedPublisher(_ => true);
        await Relay(publisher).RelayBatchAsync(CancellationToken.None);
        Assert.DoesNotContain(publisher.Sent, m => m.MessageId == key);
        Assert.Contains(publisher.Sent, m => m.MessageId == many);
    }

    [IntegrationFact]
    public async Task The_unique_key_makes_a_repeated_producer_write_once()
    {
        var key = "test:" + Guid.NewGuid().ToString("N");
        await using var ctx = CreateContext();
        var first = await InsertAsync(ctx, key);
        var second = await InsertAsync(ctx, key);
        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    private const string Empty = "{}";

    private static Task<int> InsertAsync(ApplicationDbContext ctx, string key) => ctx.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "OutboxMessages" ("Key", "Exchange", "RoutingKey", "Payload", "CreatedAt", "NextAttemptAt", "Attempts")
        VALUES ({key}, 'rnb.push', 'send', {Empty}, now(), now(), 0) ON CONFLICT ("Key") DO NOTHING
        """);

    [IntegrationFact]
    public async Task Two_relays_never_publish_the_same_row_twice()
    {
        var keys = new List<string>();
        for (var i = 0; i < 50; i++) keys.Add(await AddRowAsync());
        var a = new ScriptedPublisher(_ => true);
        var b = new ScriptedPublisher(_ => true);

        await Task.WhenAll(Relay(a).RelayBatchAsync(CancellationToken.None), Relay(b).RelayBatchAsync(CancellationToken.None));

        var all = a.Sent.Concat(b.Sent).Select(m => m.MessageId).Where(keys.Contains).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(50, all.Count);
    }
}

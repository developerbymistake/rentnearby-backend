using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Owner.Quote;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.Stays.Quotation;

// Q8 / Q13 / Q22 on a real PostgreSQL: two phones at once never share a number and never skip one; ten taps on one document
// count once; the counter equals the issues; a reserve is one indexed read plus one batch (S1 ensure, S2 lock, S3 decide).
public class QuoteReserveConcurrencyTests(TestDatabase db) : IntegrationTestBase(db)
{
    private async Task<QuoteResult<RentNearBy.Core.DTOs.Owner.Quote.QuoteReserveDto>> Reserve(Guid user, Guid doc, short kind = QuoteKinds.QuotationCode)
    {
        await using var c = CreateContext();
        return await QuoteKit.Reserve(c).ReserveAsync(user, doc, kind, default);
    }

    [IntegrationFact]
    public async Task Twenty_parallel_reserves_of_different_documents_get_twenty_consecutive_numbers()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        await using (var c = CreateContext()) await QuoteKit.PlanAsync(c, user, UserPlanStatuses.Active, DateTime.UtcNow.AddDays(20));
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => Reserve(user, Guid.NewGuid()))));
        Assert.All(results, r => Assert.True(r.Ok, r.ErrorType));
        Assert.Equal(Enumerable.Range(1, 20), results.Select(r => r.Value!.Number).OrderBy(n => n));
        Assert.All(results, r => Assert.True(r.Value!.Counted));
        await using var check = CreateContext();
        var counter = await check.StayQuoteCounters.SingleAsync(x => x.UserId == user);
        Assert.Equal((20, 21), ((int)counter.Used, counter.NextQuoteNo));
        Assert.Equal(20, await check.StayQuoteIssues.CountAsync(i => i.UserId == user));
    }

    // One counted download plus the five free uses of its window (D-3): six simultaneous taps count exactly once.
    [IntegrationFact]
    public async Task Six_parallel_taps_on_one_document_count_once_and_free_uses_are_exact()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        var doc = Guid.NewGuid();
        var taps = 1 + QuoteRules.MaxFreePerWindow;
        var results = await Task.WhenAll(Enumerable.Range(0, taps).Select(_ => Task.Run(() => Reserve(user, doc))));
        Assert.All(results, r => Assert.True(r.Ok, r.ErrorType));
        Assert.Single(results, r => r.Value!.Counted);
        Assert.All(results, r => Assert.Equal(1, r.Value!.Number));
        await using var check = CreateContext();
        Assert.Equal(1, (int)(await check.StayQuoteCounters.SingleAsync(x => x.UserId == user)).Used);
        var issue = await check.StayQuoteIssues.SingleAsync(i => i.UserId == user && i.DocumentId == doc);
        Assert.Equal((QuoteRules.MaxFreePerWindow, 1), ((int)issue.FreeUses, (int)issue.Downloads));
    }

    // Ten taps: the seventh finds no free use left and is counted again with the SAME number (new window); never a second number.
    [IntegrationFact]
    public async Task Ten_parallel_taps_on_one_document_keep_one_number()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        await using (var c = CreateContext()) await QuoteKit.PlanAsync(c, user, UserPlanStatuses.Active, DateTime.UtcNow.AddDays(20));
        var doc = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => Reserve(user, doc))));
        Assert.All(results, r => Assert.True(r.Ok, r.ErrorType));
        Assert.All(results, r => Assert.Equal(1, r.Value!.Number));
        Assert.Equal(2, results.Count(r => r.Value!.Counted));
        await using var check = CreateContext();
        Assert.Equal(1, await check.StayQuoteIssues.CountAsync(i => i.UserId == user));
        Assert.Equal(2, (int)(await check.StayQuoteCounters.SingleAsync(x => x.UserId == user)).Used);
    }

    [IntegrationFact]
    public async Task Parallel_reserves_at_the_free_cap_let_exactly_the_cap_through()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Reserve(user, Guid.NewGuid()))));
        Assert.Equal(2, results.Count(r => r.Ok));
        Assert.Equal(6, results.Count(r => r.ErrorType == QuoteErrors.MonthLimit));
        await using var check = CreateContext();
        Assert.Equal(2, (int)(await check.StayQuoteCounters.SingleAsync(x => x.UserId == user)).Used);
    }

    private sealed class CountingInterceptor : DbCommandInterceptor
    {
        public int Commands;

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Commands);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    // Q22: one EF command (the indexed pre-read) + one NpgsqlBatch of exactly three statements = two round trips.
    [IntegrationFact]
    public async Task A_reserve_is_one_read_and_one_batch()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        var counting = new CountingInterceptor();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(Db.ConnectionString!, o => o.UseNetTopologySuite())
            .AddInterceptors(counting)
            .Options;
        await using var c = new ApplicationDbContext(options);
        var r = await QuoteKit.Reserve(c).ReserveAsync(user, Guid.NewGuid(), QuoteKinds.QuotationCode, default);
        Assert.True(r.Ok);
        Assert.Equal(1, counting.Commands);
    }

    [Fact]
    public void The_batch_holds_exactly_ensure_lock_decide_with_positional_parameters()
    {
        using var connection = new Npgsql.NpgsqlConnection("Host=localhost;Database=design_only");
        using var batch = StayQuoteReserveService.BuildBatch(connection, Guid.NewGuid(), Guid.NewGuid(), QuoteKinds.ProformaCode, 202611,
            DateTime.UtcNow, 30);
        Assert.Equal(3, batch.BatchCommands.Count);
        Assert.Contains("ON CONFLICT (\"UserId\") DO NOTHING", batch.BatchCommands[0].CommandText);
        Assert.Contains("FOR UPDATE", batch.BatchCommands[1].CommandText);
        Assert.Contains("WITH prior AS", batch.BatchCommands[2].CommandText);
        Assert.Equal(8, batch.BatchCommands[2].Parameters.Count);
        var allParameters = new List<System.Data.Common.DbParameter>();
        foreach (var command in batch.BatchCommands) foreach (System.Data.Common.DbParameter parameter in command.Parameters) allParameters.Add(parameter);
        Assert.All(allParameters, p => Assert.True(string.IsNullOrEmpty(p.ParameterName)));
        Assert.Equal(QuoteRules.ReserveBatchTimeoutSeconds, batch.Timeout);
        Assert.Null(batch.Transaction);
    }

    // The decision statement never reads a stale snapshot: the lock (S2) precedes the decide (S3) in the same implicit transaction.
    [Fact]
    public void Lock_comes_before_the_decision_and_the_batch_has_no_explicit_transaction()
    {
        var src = File.ReadAllText(Path.Combine(RentNearBy.Tests.Trips.CompletionCounterGuardTests.Root(),
            "RentNearBy.Infrastructure", "Services", "Owner", "Quote", "StayQuoteReserveService.cs"));
        Assert.True(src.IndexOf("Command(LockCounterSql", StringComparison.Ordinal) < src.IndexOf("Command(DecideSql", StringComparison.Ordinal));
        Assert.DoesNotContain("BeginTransaction", src);
        Assert.Contains("PgRetry.OnceAsync", src);
    }
}

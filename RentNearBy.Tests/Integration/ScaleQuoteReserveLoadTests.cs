using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Stays.Quotation;

namespace RentNearBy.Tests.Integration;

// Q22 load run on a real PostgreSQL (RENTNEARBY_TEST_DB): RNB_SCALE_USERS owners (default 2000) reserve a quotation number at the
// same moment. Every call answers, nothing fails, every owner gets Q-0001 and one counted download. Budget RNB_SCALE_P95_MS
// (default 500 ms, the shared scale budget; ARCHITECTURE 13 targets p99 < 100 ms on the production box). See SCALE_RUNBOOK.md.
public class ScaleQuoteReserveLoadTests(TestDatabase db, ITestOutputHelper output) : IntegrationTestBase(db)
{
    private const int SetupParallelism = 16;

    [IntegrationFact]
    public async Task Reserve_AllOwnersAtOnce_NoErrorAndOneNumberEach()
    {
        var owners = ScaleSupport.Env(ScaleSupport.UsersVar, 2000);
        var budgetMs = ScaleSupport.Env(ScaleSupport.P95BudgetMsVar, 500);

        var ids = new Guid[owners];
        using (var gate = new SemaphoreSlim(SetupParallelism))
        {
            await Task.WhenAll(Enumerable.Range(0, owners).Select(async i =>
            {
                await gate.WaitAsync();
                try { ids[i] = await QuoteKit.ReadyOwnerAsync(CreateContext, $"Stay {i}"); }
                finally { gate.Release(); }
            }));
        }
        output.WriteLine($"setup: {owners} approved stay owners with accepted terms");

        var run = await ScaleSupport.DriveAsync(owners, concurrency: owners, async i =>
        {
            await using var c = CreateContext();
            var r = await QuoteKit.Reserve(c).ReserveAsync(ids[i], Guid.NewGuid(), QuoteKinds.QuotationCode, default);
            return r.Ok && r.Value!.Number == 1 && r.Value.Counted;
        });
        ScaleSupport.Report(output, "quote reserve", run);

        await using var check = CreateContext();
        var list = ids.ToList();
        Assert.Equal(0, run.Failed);
        Assert.Equal(owners, await check.StayQuoteIssues.CountAsync(i => list.Contains(i.UserId)));
        Assert.True(run.P95 <= budgetMs, $"p95 {run.P95:F0} ms over the {budgetMs} ms budget");
    }

    // The reserve reads only by primary key prefix and the purge only by the IssuedAt index (seq scans off: proves a usable index).
    [IntegrationFact]
    public async Task Reserve_and_purge_paths_have_an_index()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        await using var c = CreateContext();
        await c.Database.OpenConnectionAsync();
        await c.Database.ExecuteSqlRawAsync("SET enable_seqscan = off");
        var issues = await ScaleSupport.ExplainAnalyzeAsync(c,
            $"SELECT \"Number\" FROM \"StayQuoteIssues\" WHERE \"UserId\" = '{user}' AND \"DocumentId\" = '{Guid.NewGuid()}' AND \"Kind\" = 0");
        output.WriteLine(issues);
        Assert.Contains("Index", issues);
        var purge = await ScaleSupport.ExplainAnalyzeAsync(c,
            "SELECT \"UserId\" FROM \"StayQuoteIssues\" t WHERE t.\"IssuedAt\" < now() - interval '180 days' ORDER BY t.\"IssuedAt\" LIMIT 5000");
        output.WriteLine(purge);
        Assert.Contains("ix_stayquoteissues_issuedat", purge);
        await c.Database.ExecuteSqlRawAsync("RESET enable_seqscan");
    }
}

using Microsoft.EntityFrameworkCore;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.Integration;

// Opt-in benchmark on a real PostgreSQL: RNB_SCALE_REMINDER_ROWS pending enquiries (default 200000), the morning due scan
// paged by keyset, memory growth, and EXPLAIN ANALYZE of the due queries. Needs the RemindedOn column (B2 schema).
public class ScaleReminderRunBenchmark(TestDatabase db, ITestOutputHelper output) : IntegrationTestBase(db)
{
    private const int HostCount = 500;
    private const int PageSize = 100;
    private const int InsertBatch = 5000;

    private const string HostDuePage = """
        SELECT "OwnerUserId", count(*)::int AS pending
        FROM "TripEnquiries"
        WHERE "Status" IN ('Asked','Countered')
          AND "ExpiresAt" > now()
          AND ("RemindedOn" IS NULL OR "RemindedOn" < current_date)
          AND "OwnerUserId" > '{0}'
        GROUP BY "OwnerUserId" ORDER BY "OwnerUserId" LIMIT {1}
        """;

    private const string ExpiryPage = """
        SELECT "Id" FROM "TripEnquiries"
        WHERE "Status" IN ('Asked','Offered','Countered') AND "ExpiresAt" <= now()
        ORDER BY "ExpiresAt" LIMIT 1000
        """;

    [IntegrationFact]
    public async Task HostDueScan_OverManyPendingEnquiries_PagesByKeyset_WithBoundedMemory()
    {
        var rows = ScaleSupport.Env(ScaleSupport.RowsVar, 200_000);
        await using var h = new JourneyHarness(Db);

        var traveller = await h.AddSeekerAsync("Bench traveller");
        var hosts = new List<Guid>();
        for (var i = 0; i < HostCount; i++) hosts.Add(await h.AddStayOwnerAsync($"BenchHost{i}"));

        var tag = $"bench-{Guid.NewGuid():N}";
        try
        {
            await SeedAsync(rows, traveller, hosts, tag);
            await using (var analyze = CreateContext())
                await analyze.Database.ExecuteSqlRawAsync("ANALYZE \"TripEnquiries\"");

            var found = 0;
            var pages = 0;
            var (peakMb, growthMb) = await ScaleSupport.WithRssAsync(async () =>
            {
                var after = Guid.Empty;
                while (true)
                {
                    await using var ctx = CreateContext();
                    var conn = ctx.Database.GetDbConnection();
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = string.Format(HostDuePage, after, PageSize);
                    var page = new List<Guid>();
                    await using (var r = await cmd.ExecuteReaderAsync())
                        while (await r.ReadAsync()) page.Add(r.GetGuid(0));
                    if (page.Count == 0) break;
                    found += page.Count;
                    pages++;
                    after = page[^1];
                    if (page.Count < PageSize) break;
                }
            });
            output.WriteLine($"host due scan: rows={rows} hosts={found} pages={pages} peakRss={peakMb} MB growth={growthMb} MB");

            await using var explainCtx = CreateContext();
            output.WriteLine("EXPLAIN ANALYZE host due page:\n"
                + await ScaleSupport.ExplainAnalyzeAsync(explainCtx, string.Format(HostDuePage, Guid.Empty, PageSize)));
            var expiry = await ScaleSupport.ExplainAnalyzeAsync(explainCtx, ExpiryPage);
            output.WriteLine("EXPLAIN ANALYZE expiry page:\n" + expiry);

            Assert.Equal(HostCount, found);
            Assert.DoesNotContain("Seq Scan on \"TripEnquiries\"", expiry);
            Assert.True(growthMb < 200, $"memory grew {growthMb} MB during the scan");
        }
        finally
        {
            await using var cleanup = CreateContext();
            await cleanup.Database.ExecuteSqlRawAsync("DELETE FROM \"TripEnquiries\" WHERE \"ListingName\" = {0}", tag);
        }
    }

    private async Task SeedAsync(int rows, Guid traveller, List<Guid> hosts, string tag)
    {
        var now = DateTime.UtcNow;
        for (var done = 0; done < rows; done += InsertBatch)
        {
            await using var ctx = CreateContext();
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            var n = Math.Min(InsertBatch, rows - done);
            for (var i = 0; i < n; i++)
            {
                ctx.TripEnquiries.Add(new TripEnquiry
                {
                    Id = Guid.NewGuid(), TripId = null, TravellerUserId = traveller, OwnerUserId = hosts[(done + i) % hosts.Count],
                    Kind = TripEnquiryKinds.Stay, ListingId = Guid.NewGuid(), ListingName = tag,
                    Status = TripEnquiryStatuses.Asked, ExpiresAt = now.AddHours(72), CreatedAt = now, UpdatedAt = now,
                });
            }
            await ctx.SaveChangesAsync();
        }
        output.WriteLine($"seeded {rows} pending enquiries over {hosts.Count} hosts");
    }
}

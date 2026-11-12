using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration.Journeys;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Integration;

// Opt-in load run on a real PostgreSQL (RENTNEARBY_TEST_DB): every virtual user sends one bulk enquiry of 3 stays at the same time.
// Sizes: RNB_SCALE_USERS (default 10000), RNB_SCALE_HOSTS (default 300), budget RNB_SCALE_P95_MS (default 500). See SCALE_RUNBOOK.md.
public class ScaleBulkEnquiryLoadTests(TestDatabase db, ITestOutputHelper output) : IntegrationTestBase(db)
{
    private const int RecipientsPerRequest = 3;
    private const int SetupParallelism = 16;

    [IntegrationFact]
    public async Task BulkEnquiryOf3_AllUsersAtOnce_AnswersWithinBudget_AndNothingIsDropped()
    {
        var users = ScaleSupport.Env(ScaleSupport.UsersVar, 10_000);
        var hostCount = Math.Max(RecipientsPerRequest, ScaleSupport.Env(ScaleSupport.HostsVar, 300));
        var budgetMs = ScaleSupport.Env(ScaleSupport.P95BudgetMsVar, 500);

        await using var h = new JourneyHarness(Db);

        var stays = new Guid[hostCount];
        await ForEachBoundedAsync(hostCount, async i =>
        {
            var owner = await h.AddStayOwnerAsync($"Host{i}");
            stays[i] = await h.AddStayAsync(owner, $"Stay {i}");
        });

        var seekers = new Guid[users];
        var trips = new Guid[users];
        await ForEachBoundedAsync(users, async i =>
        {
            seekers[i] = await h.AddSeekerAsync($"Seeker{i}");
            trips[i] = await h.AddTripAsync(seekers[i], h.BuildPlan(("Nainital", 210000001, 2)));
        });
        output.WriteLine($"setup: {users} users and trips, {hostCount} stays");

        var run = await ScaleSupport.DriveAsync(users, concurrency: users, async i =>
        {
            var ids = Enumerable.Range(0, RecipientsPerRequest)
                .Select(k => stays[(i * RecipientsPerRequest + k) % hostCount]).ToList();
            var result = await h.AskAsync(trips[i], seekers[i], new CreateTripEnquiryRequest
            {
                Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 1, ListingIds = ids,
            });
            return StatusOf(result) == 201 && DataOf<TripEnquiryBatchDto>(result).Items.Count == RecipientsPerRequest;
        });
        ScaleSupport.Report(output, "bulk enquiry x3", run);

        await using var check = CreateContext();
        var ours = trips.ToList();
        var rows = await check.TripEnquiries.CountAsync(e => e.TripId != null && ours.Contains(e.TripId.Value));
        output.WriteLine($"enquiry rows written: {rows} (expected {users * RecipientsPerRequest})");

        Assert.Equal(0, run.Failed);
        Assert.Equal(users * RecipientsPerRequest, rows);
        Assert.True(run.P95 < budgetMs, $"p95 {run.P95:F0} ms is over the {budgetMs} ms budget");
    }

    private static Task ForEachBoundedAsync(int count, Func<int, Task> work)
        => Parallel.ForEachAsync(Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = SetupParallelism }, async (i, _) => await work(i));
}

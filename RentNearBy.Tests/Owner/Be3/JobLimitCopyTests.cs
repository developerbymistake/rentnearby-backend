using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be3;

namespace RentNearBy.Tests.Owner.Be3;

// Contract 3.2 JobsPosts and the plan rule: the job cap comes from the plan (1 Free, 4 Plus, 4 while payment is OFF), the refusal
// uses the one limit data shape plus "active", and after Plus ends the newest jobs stay Active and the rest go Inactive (kept,
// Close only), Active again on a new Plus.
public class JobLimitCopyTests
{
    [Fact]
    [PlanKeyTest(PlanFeatureKeys.JobsPosts)]
    public void JobsPosts_SecondOnFree_Refused_WithTheTwoMessageLines()
    {
        var free = Be3Kit.Free(ListingKinds.Stay);
        Assert.Equal(1, free.JobsPosts);
        Assert.True(free.CanUpgrade);
        Assert.Equal("Your plan allows 1 active job. Close one or upgrade.", StayJobRules.LimitMessage(free.JobsPosts, free.CanUpgrade));

        var plus = Be3Kit.Plus(ListingKinds.Stay);
        Assert.Equal(4, plus.JobsPosts);
        Assert.Equal("You can have up to 4 active jobs. Close one to post another.", StayJobRules.LimitMessage(plus.JobsPosts, plus.CanUpgrade));

        var data = StayJobRules.LimitData(free.JobsPosts, active: 1, free.CanUpgrade);
        Assert.Equal((PlanFeatureKeys.JobsPosts, 1, 1, true, 1), (data.Key, data.Limit, data.Used, data.CanUpgrade, data.Active));

        var handler = Be3Kit.Source("RentNearBy.Api", "Handlers", "StayJobHandlers.cs");
        Assert.Contains("if (active >= maxActive)", handler);
        Assert.Contains("StayJobRules.LimitData(maxActive, active, limits.CanUpgrade)", handler);
    }

    [Fact]
    public void Closing_the_active_job_turns_the_newest_inactive_job_active_at_once()
    {
        var open = new[] { Job(0, true), Job(1, true) };
        var after = PlanJobChoice.Inactive(open, Be3Kit.Free(ListingKinds.Stay).JobsPosts, hide: true);
        Assert.False(after[open[1].Id]);
        Assert.True(after[open[0].Id]);
        var handler = Be3Kit.Source("RentNearBy.Api", "Handlers", "StayJobHandlers.cs");
        Assert.Contains("var freesSlot = job.Status == StayJobStatuses.Active;", handler);
        Assert.Contains("await PlanRows.ApplyJobsAsync(db, userId, limits.JobsPosts, limits.PaymentEnabled);", handler);
    }

    [Fact]
    public void Payment_off_cap_is_four_and_never_offers_an_upgrade()
    {
        var off = Be3Kit.Off(ListingKinds.Stay);
        Assert.Equal(4, off.JobsPosts);
        Assert.False(off.CanUpgrade);
        Assert.Equal("You can have up to 4 active jobs. Close one to post another.", StayJobRules.LimitMessage(off.JobsPosts, off.CanUpgrade));
    }

    private static PlanJobRow Job(int minute, bool inactive = false)
        => new(Guid.NewGuid(), inactive, new DateTime(2026, 11, 1, 9, minute, 0, DateTimeKind.Utc));

    [Fact]
    public void Expiry_with_four_active_jobs_keeps_the_newest_active_and_three_inactive()
    {
        var jobs = Enumerable.Range(0, 4).Select(i => Job(i)).ToList();
        var free = Be3Kit.Free(ListingKinds.Stay);
        var after = PlanJobChoice.Inactive(jobs, free.JobsPosts, hide: true);
        Assert.False(after[jobs[3].Id]);
        Assert.Equal(3, after.Values.Count(v => v));
    }

    [Fact]
    public void Plus_again_makes_every_inactive_job_active_and_payment_off_hides_nothing()
    {
        var jobs = new[] { Job(0, true), Job(1, true), Job(2, true), Job(3) };
        Assert.All(PlanJobChoice.Inactive(jobs, Be3Kit.Plus(ListingKinds.Stay).JobsPosts, hide: true).Values, Assert.False);
        Assert.All(PlanJobChoice.Inactive(jobs, 1, hide: false).Values, Assert.False);
    }

    [Fact]
    public void Inactive_jobs_are_kept_off_browse_refuse_edits_and_can_be_closed()
    {
        Assert.Contains(StayJobStatuses.Inactive, StayJobStatuses.All);
        var handler = Be3Kit.Source("RentNearBy.Api", "Handlers", "StayJobHandlers.cs");
        Assert.Contains("if (job.Status == StayJobStatuses.Inactive) return OwnerHttp.RowInactive();", handler);
        Assert.Contains("if (job.Status is StayJobStatuses.Active or StayJobStatuses.Inactive)", handler);
        Assert.Contains("OverCap = activeCount > maxActive", handler);
        Assert.Equal((409, OwnerErrorTypes.PlanRowInactiveMessage),
            (OwnerErrorTypes.StatusOf(OwnerErrorTypes.PlanRowInactive), OwnerErrorTypes.MessageOf(OwnerErrorTypes.PlanRowInactive)));
        var repo = Be3Kit.Source("RentNearBy.Infrastructure", "Repositories", "StayJobRepository.cs");
        Assert.Contains(".Where(j => j.Status == StayJobStatuses.Active\n", repo.Replace("\r\n", "\n"));
        Assert.Contains("j.Status == StayJobStatuses.Active || j.Status == StayJobStatuses.Inactive", repo);
        Assert.Contains("\\\"Status\\\" IN ('Active', 'Inactive', 'Closed')", Be3Kit.Source("RentNearBy.Infrastructure", "Data", "ApplicationDbContext.cs"));
    }

    [Fact]
    public void Plan_passes_apply_the_jobs_step_on_both_paths()
    {
        var reconcile = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanLimitsReconcile.cs");
        Assert.Contains("PlanRows.ApplyJobsAsync(db, userId, limits.JobsPosts, hide, ct)", reconcile);
        Assert.Contains("StayJobsBatchSql(cte)", reconcile);
        Assert.Contains("OR jobs_act <> LEAST(jobs, ", PlanLimitsReconcile.BatchSql(ListingKinds.Stay, "pv AS (SELECT 1)"));
        Assert.Contains("\"Status\" IN ('Active', 'Inactive')", PlanRows.OpenJobsSql);
    }

    [Fact]
    public void Jobs_only_loss_gets_its_own_push_and_counts_as_hidden()
    {
        var user = Guid.NewGuid();
        var today = new DateOnly(2026, 11, 25);
        var push = StayOwnerPushCopy.PlansStarted(user, 0, 0, today, 0, plansStartedToday: false, inactiveJobs: 3);
        Assert.Equal(("Plus has ended", "3 jobs are inactive on Free. Nothing is deleted.", UserNotificationRoutes.MyJobs),
            (push.Title, push.Body, push.Route));
        Assert.Equal("1 job is inactive on Free. Nothing is deleted.", StayOwnerPushCopy.PlansStarted(user, 0, 0, today, 0, true, 1).Body);
        Assert.Equal("2 photos and 3 jobs are inactive on Free. Nothing is deleted.", StayOwnerPushCopy.PlansStarted(user, 2, 0, today, 0, true, 3).Body);
        Assert.Equal("2 photos are inactive on Free. Nothing is deleted.", StayOwnerPushCopy.PlansStarted(user, 2, 0, today).Body);

        Assert.Contains("+ (jobs - jobs_act))::int AS \"HiddenBefore\"", PlanLimitsReconcile.BatchSql(ListingKinds.Stay, "pv AS (SELECT 1)"));
        Assert.Contains("AS \"InactiveJobs\"", OwnerHiddenCounts.StaySql);
        var reconcile = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanLimitsReconcile.cs");
        Assert.Contains("h.Photos + h.Rooms + h.Routes + h.Rules + h.Jobs > before.GetValueOrDefault(h.Id)", reconcile);
        Assert.Contains("+ jobs.InactiveAfter", reconcile);
    }
}

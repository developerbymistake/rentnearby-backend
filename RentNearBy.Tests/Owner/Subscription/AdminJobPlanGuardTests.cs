using RentNearBy.Tests.Owner.Be3;

namespace RentNearBy.Tests.Owner.Subscription;

// L6 / L9 jobs: an admin close or delete takes Inactive jobs too (a re-bought Plus must not bring them back) and re-applies the
// plan jobs under the stay lock (the newest Inactive job turns Active at once), like the owner close.
public class AdminJobPlanGuardTests
{
    private static string Src => Be3Kit.Source("RentNearBy.Api", "Handlers", "AdminStayJobHandlers.cs");

    private static string Method(string name)
    {
        var s = Src;
        var at = s.IndexOf($"public static async Task<IResult> {name}(", StringComparison.Ordinal);
        Assert.True(at >= 0, name);
        var end = s.IndexOf("public static async Task<IResult>", at + 10, StringComparison.Ordinal);
        return s[at..(end < 0 ? s.Length : end)];
    }

    [Fact]
    public void Close_and_delete_take_active_and_inactive_jobs_through_the_stay_lock()
    {
        foreach (var name in new[] { "CloseJob", "DeleteJob" })
        {
            var m = Method(name);
            Assert.Contains("job.Status is StayJobStatuses.Active or StayJobStatuses.Inactive", m);
            Assert.Contains("InStayLockAsync(", m);
        }
        var s = Src;
        Assert.Contains("FOR UPDATE", s);
        Assert.Contains("PlanRows.ApplyJobsAsync(db, job.UserId, limits.JobsPosts, limits.PaymentEnabled)", s);
        Assert.Contains("OwnerHiddenCounts.ClearStayNoticeIfNothingHiddenAsync(db, notifier, job.UserId)", s);
    }
}

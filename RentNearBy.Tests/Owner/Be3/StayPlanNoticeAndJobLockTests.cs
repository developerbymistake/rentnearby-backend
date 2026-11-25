namespace RentNearBy.Tests.Owner.Be3;

// Subscription matrix fixes (BE-FIX-A): the PlanLimitsChanged notice clears only when nothing of the stay is hidden, paused or
// inactive any more (rooms, photos, special prices, jobs); a job edit re-reads the status under the stay lock; replacing a photo
// the plan hides answers 409 PlanRowInactive.
public class StayPlanNoticeAndJobLockTests
{
    private static string Between(string src, string signature, string nextMember)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = src.IndexOf(nextMember, start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? src[start..] : src[start..next];
    }

    private static string Handlers(string file) => Be3Kit.Source("RentNearBy.Api", "Handlers", file);

    [Fact]
    public void Shared_helper_counts_paused_rules_and_inactive_jobs_before_clearing()
    {
        var src = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "OwnerHiddenCounts.cs");
        var body = Between(src, "public static async Task ClearStayNoticeIfNothingHiddenAsync(", "\n    // ");
        Assert.Contains("ReadAsync(db, userId, ListingKinds.Stay, IstTime.TodayIst(DateTime.UtcNow), ct)", body);
        Assert.Contains("if (counts.Hidden.Total + counts.InactiveJobs == 0)", body);
        Assert.Contains("notifier.ClearAsync(NoticeKinds.PlanLimitsChanged, NoticeKinds.NoTrip, [userId], ct)", body);
        // Hidden.Total carries the plan-paused special prices.
        Assert.Contains("AND sp.\"PausedByPlan\" AND sp.\"DateTo\" >= @today", src);
    }

    [Fact]
    public void Photo_delete_and_room_save_never_clear_the_notice_on_the_photo_room_effect_alone()
    {
        var src = Handlers("StayHandlers.cs");
        Assert.DoesNotContain("planRows.ClearLimitsNoticeAsync", Between(src, "public static async Task<IResult> DeletePhoto(", "\n    public static "));
        Assert.DoesNotContain("planRows.ClearLimitsNoticeAsync", Between(src, "public static async Task<IResult> UpsertRooms(", "\n    public static "));
        Assert.Contains("effect.HiddenBefore > 0 && effect.HiddenAfter == 0) await ClearNoticeIfNothingHiddenAsync(db, planRows, userId);",
            Between(src, "public static async Task<IResult> DeletePhoto(", "\n    public static "));
        Assert.Contains("effect.HiddenBefore > 0 && effect.HiddenAfter == 0) await ClearNoticeIfNothingHiddenAsync(db, planRows, userId);",
            Between(src, "public static async Task<IResult> UpsertRooms(", "\n    public static "));
    }

    [Fact]
    public void Replace_of_a_photo_the_plan_hides_answers_plan_row_inactive()
    {
        var body = Between(Handlers("StayHandlers.cs"), "public static async Task<IResult> ReplacePhoto(", "\n    public ");
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.Contains("p.Id == photoId && p.StayListingId == stay.Id && p.HiddenByPlan", body);
        Assert.True(At("LockedResponse(current)") < At("OwnerHttp.RowInactive()"));
        Assert.True(At("OwnerHttp.RowInactive()") < At("NotFoundResponse(\"Photo not found\")"));
        // The new file goes on any refusal.
        Assert.True(At("OwnerHttp.RowInactive()") < At("PhotoCleanup.TryDeleteAsync(photoService, filePath, logger);"));
        Assert.Contains("return refused;", body);
    }

    [Fact]
    public void Job_edit_re_reads_the_status_under_the_stay_lock()
    {
        var src = Handlers("StayJobHandlers.cs");
        var body = Between(src, "private static async Task<IResult> UpdateCoreAsync(", "\n    // Closed is terminal");
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("CheckLookupsAsync") < At("BeginTransactionAsync"));
        Assert.True(At("BeginTransactionAsync") < At("FOR UPDATE"));
        Assert.True(At("FOR UPDATE") < At("db.StayJobs.AsNoTracking().Where(j => j.Id == id).Select(j => j.Status)"));
        Assert.True(At("Select(j => j.Status)") < At("StayJobStatuses.Inactive => OwnerHttp.RowInactive()"));
        Assert.True(At("StayJobStatuses.Inactive => OwnerHttp.RowInactive()") < At("Apply(job, request);"));
        Assert.True(At("Apply(job, request);") < At("CommitTransactionAsync"));
        Assert.Contains("_ => JobClosed()", body);
        Assert.Contains("RollbackTransactionAsync", body);
    }

    [Fact]
    public void Job_close_clears_the_notice_inside_the_transaction_for_active_and_inactive_jobs()
    {
        var src = Handlers("StayJobHandlers.cs");
        Assert.Contains("IOwnerNotifier notifier, IRateLimitService limiter, HttpContext http)", Between(src, "public static async Task<IResult> Close(", "private static"));
        var body = Between(src, "private static async Task<IResult> CloseCoreAsync(", "\n}");
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("PlanRows.ApplyJobsAsync") < At("OwnerHiddenCounts.ClearStayNoticeIfNothingHiddenAsync(db, notifier, userId)"));
        // Outside the freesSlot branch: closing an Inactive job can be the last thing inactive.
        Assert.True(At("if (freesSlot)") < At("ClearStayNoticeIfNothingHiddenAsync"));
        Assert.True(At("ClearStayNoticeIfNothingHiddenAsync") < At("CommitTransactionAsync"));
        var branch = body[At("if (freesSlot)")..At("ClearStayNoticeIfNothingHiddenAsync")];
        Assert.Equal(1, branch.Split('}').Length - 1);
    }

    [Fact]
    public void Special_price_delete_clears_the_notice_after_the_refill()
    {
        var src = Be3Kit.Source("RentNearBy.Infrastructure", "Services", "Owner", "Be2", "StaySpecialPriceService.cs");
        var body = Between(src, "public async Task<Be2Result<bool>> DeleteAsync(", "\n    public async Task");
        int At(string s) => body.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("if (deleted.Count > 0)") < At("PlanRows.ApplySpecialPricesAsync"));
        Assert.True(At("PlanRows.ApplySpecialPricesAsync") < At("OwnerHiddenCounts.ClearStayNoticeIfNothingHiddenAsync(db, notifier, me, c)"));
        Assert.True(At("ClearStayNoticeIfNothingHiddenAsync") < At("tx.CommitAsync(c)"));
    }
}

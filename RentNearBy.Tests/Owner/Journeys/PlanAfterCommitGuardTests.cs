using System.Text.RegularExpressions;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// FB-03 / FB-13 (JT-B-TX-1, JT-B-LC-2): every unit that runs plan grant or expiry effects drains the after-commit work
// once after its commit and drops it on rollback; revoke and refund tell the owner that Plus ended.
public class PlanAfterCommitGuardTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    public static TheoryData<string> EffectFiles() =>
    [
        "PlanPurchaseService.cs", "PlayBillingSyncService.cs", "PlayEntitlementApplier.cs", "PlanLifecycleService.cs",
    ];

    [Theory]
    [MemberData(nameof(EffectFiles))]
    public void Every_file_running_plan_effects_drains_after_commit(string file)
    {
        var src = Src("RentNearBy.Infrastructure", "Services", file);
        Assert.Matches("slots\\.(ApplyGrantEffectsAsync|ExpireEffectsAsync)", src);
        Assert.Contains("await slots.AfterCommitAsync()", src);
    }

    [Theory]
    [InlineData("PlanPurchaseService.cs")]
    [InlineData("PlayBillingSyncService.cs")]
    public void Every_rollback_drops_the_queued_work(string file)
    {
        var src = Src("RentNearBy.Infrastructure", "Services", file);
        var rollbacks = Regex.Matches(src, @"await tx\.RollbackAsync\(\);\r?\n\s*slots\.DropAfterCommit\(\);").Count;
        var all = Regex.Matches(src, @"await tx\.RollbackAsync\(\);").Count;
        Assert.True(all > 0);
        Assert.Equal(all, rollbacks);
    }

    [Fact]
    public void Grant_after_commit_wakes_before_the_cache_bust()
    {
        var src = Src("RentNearBy.Infrastructure", "Services", "PlanPurchaseService.cs");
        var start = src.IndexOf("private async Task AfterGrantAsync", StringComparison.Ordinal);
        var wake = src.IndexOf("await slots.AfterCommitAsync();", start, StringComparison.Ordinal);
        var bust = src.IndexOf("cacheInvalidator.BustForUserAsync", start, StringComparison.Ordinal);
        Assert.True(start > 0 && wake > start && bust > wake);
    }

    [Fact]
    public void Revoke_and_refund_write_one_plan_ended_notification_in_the_transaction()
    {
        var src = Src("RentNearBy.Infrastructure", "Services", "PlanPurchaseService.cs");
        var revoke = src[src.IndexOf("public async Task<AdminRevokeResult> AdminRevokeAsync", StringComparison.Ordinal)..];
        revoke = revoke[..revoke.IndexOf("    public async Task<AdminPurchaseDto> AdminRefundAsync", StringComparison.Ordinal)];
        Assert.Contains("PlanNotifications.Ended(userId", revoke);
        Assert.True(revoke.IndexOf("db.NotificationEvents.Add(ended)", StringComparison.Ordinal)
                    < revoke.IndexOf("await tx.CommitAsync()", StringComparison.Ordinal));
        Assert.Contains("PlanNotifications.PublishAsync(publisher, ended, logger)", revoke);

        var refund = src[src.IndexOf("private async Task<string?> ApplyRefundAsync", StringComparison.Ordinal)..];
        refund = refund[..refund.IndexOf("    private static void Cancel(", StringComparison.Ordinal)];
        Assert.Contains("refunded: true", refund);
        Assert.Contains("PlanNotifications.PublishAsync(publisher, ended, logger)", refund);
    }

    [Fact]
    public void Admin_revoke_handler_pushes_like_the_grant()
    {
        var src = Src("RentNearBy.Api", "Handlers", "AdminUserPlanHandlers.cs");
        var revoke = src[src.IndexOf("public static async Task<IResult> RevokePlan", StringComparison.Ordinal)..];
        Assert.Contains("PushNotificationAsync(hubContext, result.Notification)", revoke);
    }
}

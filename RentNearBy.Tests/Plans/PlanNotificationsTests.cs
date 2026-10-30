using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Plans;

public class PlanNotificationsTests
{
    private static readonly Guid User = TestIds.User1;

    [Fact]
    public void Activated_EveningUtc_RollsToNextIstDay()
    {
        using var _ = Culture.Invariant();

        var n = PlanNotifications.Activated(User, "Plus", Dates.Utc(2026, 11, 29, 20, 0));

        Assert.Equal(NotificationTypes.PlanActivated, n.Type);
        Assert.Equal("Plan active", n.Title);
        Assert.Equal("Plus is active till 30 Nov 2026.", n.Body);
        Assert.Equal(UserNotificationRoutes.Plans, n.ActionRoute);
        Assert.Equal(User, n.TargetUserId);
        Assert.NotEqual(Guid.Empty, n.Id);
        Assert.Null(n.ActionArgumentsJson);
    }

    [Fact]
    public void Activated_JustBeforeIstMidnight_SameDay()
    {
        using var _ = Culture.Invariant();

        var n = PlanNotifications.Activated(User, "Plus", Dates.Utc(2026, 11, 29, 18, 29, 59));

        Assert.Equal("Plus is active till 29 Nov 2026.", n.Body);
    }

    [Fact]
    public void Activated_TwoCalls_DistinctIds()
        => Assert.NotEqual(PlanNotifications.Activated(User, "Plus", Dates.Far).Id, PlanNotifications.Activated(User, "Plus", Dates.Far).Id);

    [Fact]
    public void Expiring_RenewCopy()
    {
        using var _ = Culture.Invariant();

        var n = PlanNotifications.Expiring(User, "Plus", Dates.Utc(2026, 12, 1, 6, 0));

        Assert.Equal(NotificationTypes.PlanExpiring, n.Type);
        Assert.Equal("Plan ends soon", n.Title);
        Assert.Equal("Plus ends on 01 Dec 2026. Renew to keep your benefits.", n.Body);
    }

    [Fact]
    public void ExpiringCancelled_ResubscribeCopy()
    {
        using var _ = Culture.Invariant();

        var n = PlanNotifications.ExpiringCancelled(User, "Plus", Dates.Utc(2026, 12, 1, 6, 0));

        Assert.Equal(NotificationTypes.PlanExpiring, n.Type);
        Assert.Equal("Plus ends on 01 Dec 2026. Resubscribe to keep your benefits.", n.Body);
    }

    [Theory]
    [InlineData(true, "Subscription refunded. You are on the free plan now.")]
    [InlineData(false, "Subscription ended. You are on the free plan now.")]
    public void Ended_Refunded_PicksCopy(bool refunded, string expectedBody)
    {
        var n = PlanNotifications.Ended(User, "Plus", refunded);

        Assert.Equal(NotificationTypes.PlanExpired, n.Type);
        Assert.Equal("Plan ended", n.Title);
        Assert.Equal(expectedBody, n.Body);
    }

    [Fact]
    public void Paused_NoResumeDate_PointsToPlay()
    {
        var n = PlanNotifications.Paused(User, "Plus", null);

        Assert.Equal(NotificationTypes.PlanPaused, n.Type);
        Assert.Equal("Subscription paused", n.Title);
        Assert.Equal("Resume it in Google Play.", n.Body);
    }

    [Fact]
    public void Paused_WithResumeDate_ShowsIstDay()
    {
        using var _ = Culture.Invariant();

        Assert.Equal("Resumes on 05 Dec 2026.", PlanNotifications.Paused(User, "Plus", Dates.Utc(2026, 12, 5)).Body);
    }

    [Fact]
    public void CarryOverActivated_DaysAndPlan()
    {
        using var _ = Culture.Invariant();

        var n = PlanNotifications.CarryOverActivated(User, "Plus", 9, Dates.Utc(2026, 12, 10));

        Assert.Equal(NotificationTypes.PlanCarryOverActivated, n.Type);
        Assert.Equal("Bonus days active", n.Title);
        Assert.Equal("9 bonus days on Plus till 10 Dec 2026.", n.Body);
    }

    [Fact]
    public void AdminGrantCarryOver_IsActivatedType()
    {
        var n = PlanNotifications.AdminGrantCarryOver(User, 9);

        Assert.Equal(NotificationTypes.PlanActivated, n.Type);
        Assert.Equal("Days added", n.Title);
        Assert.Equal("+9 days added after your subscription.", n.Body);
        Assert.Equal(UserNotificationRoutes.Plans, n.ActionRoute);
    }

    [Fact]
    public void Expired_And_Renewed_And_OnHold_Copy()
    {
        using var _ = Culture.Invariant();

        Assert.Equal("Plus has ended. You are on the free plan now.", PlanNotifications.Expired(User, "Plus").Body);
        Assert.Equal("Plus renewed till 01 Dec 2026.", PlanNotifications.Renewed(User, "Plus", Dates.Utc(2026, 12, 1)).Body);
        Assert.Equal("Plus is paused until the payment is fixed in Google Play.", PlanNotifications.OnHold(User, "Plus").Body);
        Assert.Equal(NotificationTypes.PlanPaymentIssue, PlanNotifications.PaymentIssue(User, "Plus").Type);
    }
}

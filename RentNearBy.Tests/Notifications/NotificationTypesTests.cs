namespace RentNearBy.Tests.Notifications;

public class NotificationTypesTests
{
    [Theory]
    [InlineData(NotificationTypes.GoLiveApproved, "golive_approved")]
    [InlineData(NotificationTypes.GoLiveRejected, "golive_rejected")]
    [InlineData(NotificationTypes.RoleRequestApproved, "role_request_approved")]
    [InlineData(NotificationTypes.RoleRequestRejected, "role_request_rejected")]
    [InlineData(NotificationTypes.StayApproved, "stay_approved")]
    [InlineData(NotificationTypes.StayRejected, "stay_rejected")]
    [InlineData(NotificationTypes.TaxiApproved, "taxi_approved")]
    [InlineData(NotificationTypes.TaxiRejected, "taxi_rejected")]
    [InlineData(NotificationTypes.PlanActivated, "plan_activated")]
    [InlineData(NotificationTypes.PlanExpiring, "plan_expiring")]
    [InlineData(NotificationTypes.PlanExpired, "plan_expired")]
    [InlineData(NotificationTypes.PlanRenewed, "plan_renewed")]
    [InlineData(NotificationTypes.PlanPaymentIssue, "plan_payment_issue")]
    [InlineData(NotificationTypes.PlanOnHold, "plan_on_hold")]
    [InlineData(NotificationTypes.PlanPaused, "plan_paused")]
    [InlineData(NotificationTypes.PlanCarryOverActivated, "plan_carry_over_activated")]
    [InlineData(NotificationTypes.StayJobPosted, "stay_job_posted")]
    [InlineData(NotificationTypes.PlanPurchased, "plan_purchased")]
    [InlineData(NotificationTypes.PlanChargebackReview, "plan_chargeback_review")]
    public void ToWireValue_KnownType_SnakeValue(string type, string expected)
        => Assert.Equal(expected, NotificationTypes.ToWireValue(type));

    [Theory]
    [InlineData("SomethingNew")]
    [InlineData("")]
    public void ToWireValue_UnknownType_Unchanged(string type)
        => Assert.Equal(type, NotificationTypes.ToWireValue(type));
}

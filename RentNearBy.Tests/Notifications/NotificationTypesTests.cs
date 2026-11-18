namespace RentNearBy.Tests.Notifications;

public class NotificationTypesTests
{
    [Theory]
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
    [InlineData(NotificationTypes.TaxiDriverAccepted, "taxi_driver_accepted")]
    [InlineData(NotificationTypes.TaxiSelectionLapsed, "taxi_selection_lapsed")]
    [InlineData(NotificationTypes.TaxiBooked, "taxi_booked")]
    [InlineData(NotificationTypes.TaxiRequestEnded, "taxi_request_ended")]
    [InlineData(NotificationTypes.TaxiDriverWithdrew, "taxi_driver_withdrew")]
    [InlineData(NotificationTypes.TaxiRateDriver, "taxi_rate_driver")]
    [InlineData(NotificationTypes.TaxiTripEnded, "taxi_trip_ended")]
    [InlineData(NotificationTypes.TaxiSelected, "taxi_selected")]
    [InlineData(NotificationTypes.TaxiNotSelected, "taxi_not_selected")]
    [InlineData(NotificationTypes.TaxiRequestClosed, "taxi_request_closed")]
    [InlineData(NotificationTypes.TaxiBookingCancelled, "taxi_booking_cancelled")]
    [InlineData(NotificationTypes.TaxiRingsMissed, "taxi_rings_missed")]
    [InlineData(NotificationTypes.TaxiRequestRing, "taxi_request_ring")]
    [InlineData(NotificationTypes.TaxiRequestRingEnded, "taxi_request_ring_ended")]
    public void ToWireValue_KnownType_SnakeValue(string type, string expected)
        => Assert.Equal(expected, NotificationTypes.ToWireValue(type));

    [Theory]
    [InlineData("SomethingNew")]
    [InlineData("")]
    public void ToWireValue_UnknownType_Unchanged(string type)
        => Assert.Equal(type, NotificationTypes.ToWireValue(type));

    [Fact]
    public void Taxi_request_types_are_the_twelve_notices_plus_ring_and_ring_end()
    {
        var names = typeof(NotificationTypes).GetFields().Where(f => f.IsLiteral && f.Name.StartsWith("Taxi") && f.Name is not ("TaxiApproved" or "TaxiRejected")).Select(f => f.Name).ToArray();
        Assert.Equal(14, names.Length);
        Assert.All(names, n => Assert.Equal(n, (string)typeof(NotificationTypes).GetField(n)!.GetRawConstantValue()!));
    }
}

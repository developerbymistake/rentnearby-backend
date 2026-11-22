namespace RentNearBy.Core.Models.TaxiRequests;

// FB-46: one ring rule. CanBeRung mirrors TaxiCandidateQuery.RingRule (the matcher pool): notifications on (or never tested, the
// legacy path), under the missed-rings limit, and a valid device token. Home shows "ready" only after the readiness test passed
// AND the matcher could ring him, so a driver with notifications off is never told he is ready.
public static class TaxiRingRules
{
    public static bool CanBeRung(DateTime? ringReadyAt, bool notificationsOn, int missStreak, bool hasValidToken)
        => (ringReadyAt == null || notificationsOn) && missStreak < TaxiDispatchRules.RingMissLimit && hasValidToken;

    public static bool HomeReady(DateTime? ringReadyAt, bool notificationsOn, int missStreak, bool hasValidToken)
        => ringReadyAt != null && CanBeRung(ringReadyAt, notificationsOn, missStreak, hasValidToken);
}

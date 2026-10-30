namespace RentNearBy.Core.Models;

// Discriminator for NotificationEvent.Type — plain constants, no DB lookup table (matches
// ListingKinds's established idiom; a lookup table only pays
// for itself if types need to be admin-configurable at runtime, which isn't the case here). Add a
// new const here (and a matching wire-value entry below) for each new notification category —
// nothing else about the schema changes.
public static class NotificationTypes
{
    public const string GoLiveApproved = "GoLiveApproved";
    public const string GoLiveRejected = "GoLiveRejected";
    public const string RoleRequestApproved = "RoleRequestApproved";
    public const string RoleRequestRejected = "RoleRequestRejected";
    public const string StayApproved = "StayApproved";
    public const string StayRejected = "StayRejected";
    public const string TaxiApproved = "TaxiApproved";
    public const string TaxiRejected = "TaxiRejected";
    public const string PlanActivated = "PlanActivated";
    public const string PlanExpiring = "PlanExpiring";
    public const string PlanExpired = "PlanExpired";
    public const string PlanRenewed = "PlanRenewed";
    public const string PlanPaymentIssue = "PlanPaymentIssue";
    public const string PlanOnHold = "PlanOnHold";
    public const string PlanPaused = "PlanPaused";
    public const string PlanCarryOverActivated = "PlanCarryOverActivated";
    // Push-only (FCM notification_type for the seeker and admin job pushes), no NotificationEvent row.
    public const string StayJobPosted = "StayJobPosted";

    // The lowercase-snake value sent as the FCM data payload's "notification_type" key — kept
    // paired here (not derived by naive case-conversion) so the PascalCase DB value and the wire
    // value can never silently diverge; notification_service.dart's _handleNotificationTap
    // switches on this exact string for its legacy per-type fallback.
    private static readonly Dictionary<string, string> WireValues = new()
    {
        [GoLiveApproved] = "golive_approved",
        [GoLiveRejected] = "golive_rejected",
        [RoleRequestApproved] = "role_request_approved",
        [RoleRequestRejected] = "role_request_rejected",
        [StayApproved] = "stay_approved",
        [StayRejected] = "stay_rejected",
        [TaxiApproved] = "taxi_approved",
        [TaxiRejected] = "taxi_rejected",
        [PlanActivated] = "plan_activated",
        [PlanExpiring] = "plan_expiring",
        [PlanExpired] = "plan_expired",
        [PlanRenewed] = "plan_renewed",
        [PlanPaymentIssue] = "plan_payment_issue",
        [PlanOnHold] = "plan_on_hold",
        [PlanPaused] = "plan_paused",
        [PlanCarryOverActivated] = "plan_carry_over_activated",
        [StayJobPosted] = "stay_job_posted",
    };

    public static string ToWireValue(string type) => WireValues.TryGetValue(type, out var wire) ? wire : type;
}

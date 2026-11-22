namespace RentNearBy.Core.Models;

// Discriminator for NotificationEvent.Type — plain constants, no DB lookup table (matches
// ListingKinds's established idiom; a lookup table only pays
// for itself if types need to be admin-configurable at runtime, which isn't the case here). Add a
// new const here (and a matching wire-value entry below) for each new notification category —
// nothing else about the schema changes.
public static class NotificationTypes
{
    public const string RoleRequestApproved = "RoleRequestApproved";
    public const string RoleRequestRejected = "RoleRequestRejected";
    public const string StayApproved = "StayApproved";
    public const string StayRejected = "StayRejected";
    public const string TaxiApproved = "TaxiApproved";
    public const string TaxiRejected = "TaxiRejected";
    // Admin removed only the taxi display photo (the taxi stays live).
    public const string DriverPhotoRemoved = "DriverPhotoRemoved";
    // Admin removed the user's profile picture; the app asks for a new one.
    public const string ProfilePhotoRemoved = "ProfilePhotoRemoved";
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
    // Push-only admin pushes (plan.purchase.admin), no NotificationEvent row.
    public const string PlanPurchased = "PlanPurchased";
    public const string PlanChargebackReview = "PlanChargebackReview";
    public const string TripEnquiryReceived = "TripEnquiryReceived";
    public const string TripOfferReceived = "TripOfferReceived";
    public const string TripOfferAccepted = "TripOfferAccepted";
    public const string TripOfferDeclined = "TripOfferDeclined";
    public const string TripListingRemoved = "TripListingRemoved";
    public const string TripTravellerLeft = "TripTravellerLeft";
    public const string TripEnquiryExpired = "TripEnquiryExpired";
    public const string TripNotAvailable = "TripNotAvailable";
    public const string TripTravellerCancelled = "TripTravellerCancelled";
    public const string TripOwnerCancelled = "TripOwnerCancelled";
    public const string TripLeadReceived = "TripLeadReceived";
    public const string TripDriverFare = "TripDriverFare";
    public const string TripTaxiBookedByTraveller = "TripTaxiBookedByTraveller";
    public const string TripBookingChanged = "TripBookingChanged";
    public const string TripDriverConfirmed = "TripDriverConfirmed";
    public const string TripDriverDenied = "TripDriverDenied";
    public const string TripEnquiryClosedByAdmin = "TripEnquiryClosedByAdmin";
    public const string TripCounterReceived = "TripCounterReceived";
    public const string TripRevisedOffer = "TripRevisedOffer";
    public const string TripRevisedKept = "TripRevisedKept";
    public const string TripChangeRequest = "TripChangeRequest";
    public const string TripChangeAccepted = "TripChangeAccepted";
    public const string TripNoReply = "TripNoReply";
    public const string TripProblemReported = "TripProblemReported";
    public const string TripStartsIn3Days = "TripStartsIn3Days";
    public const string TripStartsToday = "TripStartsToday";
    public const string TripMarkComplete = "TripMarkComplete";
    public const string TripTravelEve = "TripTravelEve";
    // Push-only morning reminders (rnb.push outbox), no NotificationEvent row.
    public const string TripReminder = "TripReminder";
    // Taxi request system: the 12 visible notices of the contract table 10.3 plus the choose reminder (O7), then the push-only ring and ring end.
    public const string TaxiDriverAccepted = "TaxiDriverAccepted";
    public const string TaxiSelectionLapsed = "TaxiSelectionLapsed";
    public const string TaxiBooked = "TaxiBooked";
    public const string TaxiRequestEnded = "TaxiRequestEnded";
    public const string TaxiDriverWithdrew = "TaxiDriverWithdrew";
    public const string TaxiRateDriver = "TaxiRateDriver";
    public const string TaxiTripEnded = "TaxiTripEnded";
    public const string TaxiSelected = "TaxiSelected";
    public const string TaxiNotSelected = "TaxiNotSelected";
    public const string TaxiRequestClosed = "TaxiRequestClosed";
    public const string TaxiBookingCancelled = "TaxiBookingCancelled";
    public const string TaxiRingsMissed = "TaxiRingsMissed";
    public const string TaxiChooseDriver = "TaxiChooseDriver";
    public const string TaxiRequestRing = "TaxiRequestRing";
    public const string TaxiRequestRingEnded = "TaxiRequestRingEnded";
    // Buddies, trip sharing and expenses: push-only (outbox, NotificationId null); stored rows live in UserNotices.
    public const string BuddyRequestReceived = "BuddyRequestReceived";
    public const string BuddyRequestAccepted = "BuddyRequestAccepted";
    public const string TripInvite = "TripInvite";
    public const string TripMemberJoined = "TripMemberJoined";
    public const string TripPlanUpdated = "TripPlanUpdated";
    public const string TripMemberRemoved = "TripMemberRemoved";
    public const string TripDeletedForViewer = "TripDeletedForViewer";
    public const string TripExpensesUpdated = "TripExpensesUpdated";
    public const string TripSettleReminder = "TripSettleReminder";
    public const string TripPaymentSaidPaid = "TripPaymentSaidPaid";
    public const string TripPaymentReceived = "TripPaymentReceived";
    public const string TripPaymentWithdrawn = "TripPaymentWithdrawn";
    public const string TripPaymentNotReceived = "TripPaymentNotReceived";
    // Owner roles (contract 5.3, R-0): outbox pushes with NotificationId null; the stored ones live in UserNotices.
    public const string StayEditApproved = "StayEditApproved";
    public const string StayEditRejected = "StayEditRejected";
    public const string FestivalReminder = "FestivalReminder";
    public const string PlanLimitsChanged = "PlanLimitsChanged";
    public const string OwnerMorningReminder = "OwnerMorningReminder";
    public const string PlansStarting = "PlansStarting";

    public static readonly IReadOnlyList<string> OwnerPushKinds =
    [
        StayEditApproved, StayEditRejected, FestivalReminder, PlanLimitsChanged, OwnerMorningReminder, PlansStarting,
    ];

    public static readonly IReadOnlyList<string> BuddyPushKinds =
    [
        BuddyRequestReceived, BuddyRequestAccepted, TripInvite, TripMemberJoined, TripPlanUpdated, TripMemberRemoved,
        TripDeletedForViewer, TripExpensesUpdated, TripSettleReminder, TripPaymentSaidPaid, TripPaymentReceived,
        TripPaymentWithdrawn, TripPaymentNotReceived,
    ];

    // The lowercase-snake value sent as the FCM data payload's "notification_type" key — kept
    // paired here (not derived by naive case-conversion) so the PascalCase DB value and the wire
    // value can never silently diverge; notification_service.dart's _handleNotificationTap
    // switches on this exact string for its legacy per-type fallback.
    private static readonly Dictionary<string, string> WireValues = new()
    {
        [RoleRequestApproved] = "role_request_approved",
        [RoleRequestRejected] = "role_request_rejected",
        [StayApproved] = "stay_approved",
        [StayRejected] = "stay_rejected",
        [TaxiApproved] = "taxi_approved",
        [TaxiRejected] = "taxi_rejected",
        [DriverPhotoRemoved] = "driver_photo_removed",
        [ProfilePhotoRemoved] = "profile_photo_removed",
        [PlanActivated] = "plan_activated",
        [PlanExpiring] = "plan_expiring",
        [PlanExpired] = "plan_expired",
        [PlanRenewed] = "plan_renewed",
        [PlanPaymentIssue] = "plan_payment_issue",
        [PlanOnHold] = "plan_on_hold",
        [PlanPaused] = "plan_paused",
        [PlanCarryOverActivated] = "plan_carry_over_activated",
        [StayJobPosted] = "stay_job_posted",
        [PlanPurchased] = "plan_purchased",
        [PlanChargebackReview] = "plan_chargeback_review",
        [TripEnquiryReceived] = "trip_enquiry_received",
        [TripOfferReceived] = "trip_offer_received",
        [TripOfferAccepted] = "trip_offer_accepted",
        [TripOfferDeclined] = "trip_offer_declined",
        [TripListingRemoved] = "trip_listing_removed",
        [TripTravellerLeft] = "trip_traveller_left",
        [TripEnquiryExpired] = "trip_enquiry_expired",
        [TripNotAvailable] = "trip_not_available",
        [TripTravellerCancelled] = "trip_traveller_cancelled",
        [TripOwnerCancelled] = "trip_owner_cancelled",
        [TripLeadReceived] = "trip_lead_received",
        [TripDriverFare] = "trip_driver_fare",
        [TripTaxiBookedByTraveller] = "trip_taxi_booked_by_traveller",
        [TripBookingChanged] = "trip_booking_changed",
        [TripDriverConfirmed] = "trip_driver_confirmed",
        [TripDriverDenied] = "trip_driver_denied",
        [TripEnquiryClosedByAdmin] = "trip_enquiry_closed_by_admin",
        [TripCounterReceived] = "trip_counter_received",
        [TripRevisedOffer] = "trip_revised_offer",
        [TripRevisedKept] = "trip_revised_kept",
        [TripChangeRequest] = "trip_change_request",
        [TripChangeAccepted] = "trip_change_accepted",
        [TripNoReply] = "trip_no_reply",
        [TripProblemReported] = "trip_problem_reported",
        [TripStartsIn3Days] = "trip_starts_in_3_days",
        [TripStartsToday] = "trip_starts_today",
        [TripMarkComplete] = "trip_mark_complete",
        [TripTravelEve] = "trip_travel_eve",
        [TripReminder] = "trip_reminder",
        [TaxiDriverAccepted] = "taxi_driver_accepted",
        [TaxiSelectionLapsed] = "taxi_selection_lapsed",
        [TaxiBooked] = "taxi_booked",
        [TaxiRequestEnded] = "taxi_request_ended",
        [TaxiDriverWithdrew] = "taxi_driver_withdrew",
        [TaxiRateDriver] = "taxi_rate_driver",
        [TaxiTripEnded] = "taxi_trip_ended",
        [TaxiSelected] = "taxi_selected",
        [TaxiNotSelected] = "taxi_not_selected",
        [TaxiRequestClosed] = "taxi_request_closed",
        [TaxiBookingCancelled] = "taxi_booking_cancelled",
        [TaxiRingsMissed] = "taxi_rings_missed",
        [TaxiChooseDriver] = "taxi_choose_driver",
        [TaxiRequestRing] = "taxi_request_ring",
        [TaxiRequestRingEnded] = "taxi_request_ring_ended",
        [BuddyRequestReceived] = "buddy_request_received",
        [BuddyRequestAccepted] = "buddy_request_accepted",
        [TripInvite] = "trip_invite",
        [TripMemberJoined] = "trip_member_joined",
        [TripPlanUpdated] = "trip_plan_updated",
        [TripMemberRemoved] = "trip_member_removed",
        [TripDeletedForViewer] = "trip_deleted_for_viewer",
        [TripExpensesUpdated] = "trip_expenses_updated",
        [TripSettleReminder] = "trip_settle_reminder",
        [TripPaymentSaidPaid] = "trip_payment_said_paid",
        [TripPaymentReceived] = "trip_payment_received",
        [TripPaymentWithdrawn] = "trip_payment_withdrawn",
        [TripPaymentNotReceived] = "trip_payment_not_received",
        [StayEditApproved] = "stay_edit_approved",
        [StayEditRejected] = "stay_edit_rejected",
        [FestivalReminder] = "festival_reminder",
        [PlanLimitsChanged] = "plan_limits_changed",
        [OwnerMorningReminder] = "owner_morning_reminder",
        [PlansStarting] = "plans_starting",
    };

    public static string ToWireValue(string type) => WireValues.TryGetValue(type, out var wire) ? wire : type;
}

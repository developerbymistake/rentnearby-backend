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
    // Push-only admin pushes (plan.purchase.admin), no NotificationEvent row.
    public const string PlanPurchased = "PlanPurchased";
    public const string PlanChargebackReview = "PlanChargebackReview";
    public const string TripEnquiryReceived = "TripEnquiryReceived";
    public const string TripOfferReceived = "TripOfferReceived";
    public const string TripOfferAccepted = "TripOfferAccepted";
    public const string TripOfferDeclined = "TripOfferDeclined";
    public const string TripPhoneBookingRecorded = "TripPhoneBookingRecorded";
    public const string TripHostConfirmed = "TripHostConfirmed";
    public const string TripHostDenied = "TripHostDenied";
    public const string TripHostLinkAnswered = "TripHostLinkAnswered";
    public const string TripListingRemoved = "TripListingRemoved";
    public const string TripTravellerLeft = "TripTravellerLeft";
    public const string TripEnquiryExpired = "TripEnquiryExpired";
    public const string TripNotAvailable = "TripNotAvailable";
    public const string TripTravellerCancelled = "TripTravellerCancelled";
    public const string TripOwnerCancelled = "TripOwnerCancelled";
    public const string TripLeadReceived = "TripLeadReceived";
    public const string TripAdvanceRequested = "TripAdvanceRequested";
    public const string TripAdvanceProofSent = "TripAdvanceProofSent";
    public const string TripAdvanceNotYet = "TripAdvanceNotYet";
    public const string TripAdvanceReceived = "TripAdvanceReceived";
    public const string TripHoldExpired = "TripHoldExpired";
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
    public const string TripHostNotConfirmed = "TripHostNotConfirmed";
    public const string TripNoReply = "TripNoReply";
    public const string TripProblemReported = "TripProblemReported";
    public const string TripStartsIn3Days = "TripStartsIn3Days";
    public const string TripStartsToday = "TripStartsToday";
    public const string TripMarkComplete = "TripMarkComplete";
    public const string TripTravelEve = "TripTravelEve";

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
        [PlanPurchased] = "plan_purchased",
        [PlanChargebackReview] = "plan_chargeback_review",
        [TripEnquiryReceived] = "trip_enquiry_received",
        [TripOfferReceived] = "trip_offer_received",
        [TripOfferAccepted] = "trip_offer_accepted",
        [TripOfferDeclined] = "trip_offer_declined",
        [TripPhoneBookingRecorded] = "trip_phone_booking_recorded",
        [TripHostConfirmed] = "trip_host_confirmed",
        [TripHostDenied] = "trip_host_denied",
        [TripHostLinkAnswered] = "trip_host_link_answered",
        [TripListingRemoved] = "trip_listing_removed",
        [TripTravellerLeft] = "trip_traveller_left",
        [TripEnquiryExpired] = "trip_enquiry_expired",
        [TripNotAvailable] = "trip_not_available",
        [TripTravellerCancelled] = "trip_traveller_cancelled",
        [TripOwnerCancelled] = "trip_owner_cancelled",
        [TripLeadReceived] = "trip_lead_received",
        [TripAdvanceRequested] = "trip_advance_requested",
        [TripAdvanceProofSent] = "trip_advance_proof_sent",
        [TripAdvanceNotYet] = "trip_advance_not_yet",
        [TripAdvanceReceived] = "trip_advance_received",
        [TripHoldExpired] = "trip_hold_expired",
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
        [TripHostNotConfirmed] = "trip_host_not_confirmed",
        [TripNoReply] = "trip_no_reply",
        [TripProblemReported] = "trip_problem_reported",
        [TripStartsIn3Days] = "trip_starts_in_3_days",
        [TripStartsToday] = "trip_starts_today",
        [TripMarkComplete] = "trip_mark_complete",
        [TripTravelEve] = "trip_travel_eve",
    };

    public static string ToWireValue(string type) => WireValues.TryGetValue(type, out var wire) ? wire : type;
}

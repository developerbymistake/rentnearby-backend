namespace RentNearBy.Core.Models;

// Traveller (seeker) features: the SQL fragment that keeps the other person of a buddy / trip sharing statement to a seeker
// account (a non-seeker reads exactly like an unknown or inactive person), and the notice kinds, inbox types and pushes an
// account that is no longer a seeker does not get. Nothing is deleted: a seeker again sees everything back.
public static class TravellerOnly
{
    // Equal to UserRoles.SeekerId (pinned by a test); a constant because several statements are const strings.
    public const string SeekerRoleSql = "'10000000-0000-0000-0000-000000000001'";

    // UserNotices kinds of the buddy / trip sharing / money feature (every kind that is not an owner kind).
    public static readonly IReadOnlyList<string> NoticeKinds =
        global::RentNearBy.Core.Models.Buddy.NoticeKinds.All.Except(global::RentNearBy.Core.Models.Buddy.NoticeKinds.Owner).ToList();

    // NotificationEvent types whose only receiver is the traveller of a trip, a taxi request or the buddy feature.
    public static readonly IReadOnlyList<string> NotificationTypes =
    [
        global::RentNearBy.Core.Models.NotificationTypes.TripOfferReceived,
        global::RentNearBy.Core.Models.NotificationTypes.TripListingRemoved,
        global::RentNearBy.Core.Models.NotificationTypes.TripEnquiryExpired,
        global::RentNearBy.Core.Models.NotificationTypes.TripNotAvailable,
        global::RentNearBy.Core.Models.NotificationTypes.TripOwnerCancelled,
        global::RentNearBy.Core.Models.NotificationTypes.TripDriverFare,
        global::RentNearBy.Core.Models.NotificationTypes.TripDriverConfirmed,
        global::RentNearBy.Core.Models.NotificationTypes.TripDriverDenied,
        global::RentNearBy.Core.Models.NotificationTypes.TripRevisedOffer,
        global::RentNearBy.Core.Models.NotificationTypes.TripChangeAccepted,
        global::RentNearBy.Core.Models.NotificationTypes.TripNoReply,
        global::RentNearBy.Core.Models.NotificationTypes.TripStartsIn3Days,
        global::RentNearBy.Core.Models.NotificationTypes.TripStartsToday,
        global::RentNearBy.Core.Models.NotificationTypes.TripMarkComplete,
        global::RentNearBy.Core.Models.NotificationTypes.TripTravelEve,
        global::RentNearBy.Core.Models.NotificationTypes.TaxiDriverAccepted,
        global::RentNearBy.Core.Models.NotificationTypes.TaxiSelectionLapsed,
        global::RentNearBy.Core.Models.NotificationTypes.TaxiBooked,
        global::RentNearBy.Core.Models.NotificationTypes.TaxiRequestEnded,
        global::RentNearBy.Core.Models.NotificationTypes.TaxiDriverWithdrew,
        global::RentNearBy.Core.Models.NotificationTypes.TaxiRateDriver,
        global::RentNearBy.Core.Models.NotificationTypes.TaxiChooseDriver,
        .. global::RentNearBy.Core.Models.NotificationTypes.BuddyPushKinds,
    ];

    // Push action routes that open a traveller page (trips, trip taxi, buddies, sharing, money, journeys).
    public static readonly IReadOnlySet<string> PushRoutes = new HashSet<string>(StringComparer.Ordinal)
    {
        UserNotificationRoutes.TripPlan, UserNotificationRoutes.MyTrips, UserNotificationRoutes.TripTaxiReplies,
        UserNotificationRoutes.TripName, UserNotificationRoutes.TripCustomize, UserNotificationRoutes.TripOverview,
        UserNotificationRoutes.TripTaxiRequest, UserNotificationRoutes.TripTaxiStatus, UserNotificationRoutes.TripReplies,
        UserNotificationRoutes.Buddies, UserNotificationRoutes.TripInvite, UserNotificationRoutes.TripTravellers,
        UserNotificationRoutes.TripView, UserNotificationRoutes.TripExpenses, UserNotificationRoutes.TripSettle,
        JourneyNotifications.Route,
        UserNotificationRoutes.People, UserNotificationRoutes.Traveller, UserNotificationRoutes.JoinRequests,
        UserNotificationRoutes.TripPost, UserNotificationRoutes.TravelBuddies, UserNotificationRoutes.DmRoom,
        UserNotificationRoutes.SeekerPlus,
    };

    private static readonly HashSet<string> PushTypes = new(NotificationTypes, StringComparer.Ordinal);

    // A push only a seeker receives: a traveller type, or any type whose tap opens a traveller page.
    public static bool IsTravellerPush(string? type, string? route)
        => (type != null && PushTypes.Contains(type)) || (route != null && PushRoutes.Contains(route));
}

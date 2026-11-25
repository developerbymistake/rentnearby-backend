namespace RentNearBy.Core.Models;

// Honest end label of a stay or taxi row (null while open); the app maps it, displayStatus is unchanged for old builds.
public static class TripEndReasons
{
    // New FailReason values (max 24 chars), written on NotAvailable rows.
    public const string StayPaused = "StayPaused";
    public const string HostBlocked = "HostBlocked";

    public const string Withdrawn = "withdrawn";
    public const string BookedElsewhere = "bookedElsewhere";
    public const string DatesChanged = "datesChanged";
    public const string TripDeleted = "tripDeleted";
    public const string AccountDeleted = "accountDeleted";
    public const string AdminClosed = "adminClosed";
    public const string StayRemoved = "stayRemoved";
    public const string StayPausedReason = "stayPaused";
    public const string HostBlockedReason = "hostBlocked";
    public const string HostNotAvailable = "hostNotAvailable";
    public const string OfferExpired = "offerExpired";
    public const string NoReply = "noReply";
    public const string DeclinedByYou = "declinedByYou";
    public const string CancelledByGuest = "cancelledByGuest";
    public const string CancelledByHost = "cancelledByHost";
    public const string Problem = "problem";
    public const string Closed = "closed";

    public static string? Of(string status, string? failReason, bool hadExpiredOffer)
    {
        if (TripEnquiryStatuses.IsOpen(status)) return null;
        return status switch
        {
            TripEnquiryStatuses.Closed => failReason switch
            {
                TripCloseReasons.Withdrawn => Withdrawn,
                TripCloseReasons.BookedElsewhere => BookedElsewhere,
                TripCloseReasons.TripDatesChanged => DatesChanged,
                TripCloseReasons.TripDeleted => TripDeleted,
                TripCloseReasons.AccountDeleted => AccountDeleted,
                TripCloseReasons.AdminClosed => AdminClosed,
                _ => Closed,
            },
            TripEnquiryStatuses.NotAvailable => failReason switch
            {
                TripFailReasons.ListingRemoved => StayRemoved,
                StayPaused => StayPausedReason,
                HostBlocked => HostBlockedReason,
                _ => HostNotAvailable,
            },
            TripEnquiryStatuses.CancelledByOwner => failReason == TripFailReasons.ListingRemoved ? StayRemoved : CancelledByHost,
            TripEnquiryStatuses.Expired => hadExpiredOffer ? OfferExpired : NoReply,
            TripEnquiryStatuses.Declined => DeclinedByYou,
            TripEnquiryStatuses.CancelledByTraveller => CancelledByGuest,
            TripEnquiryStatuses.Failed => Problem,
            _ => Closed,
        };
    }
}

// Traveller My enquiries boxes; mirrors TripOwnerBuckets. Stay rows only.
public static class TripMyBuckets
{
    public const string Waiting = "waiting";
    public const string Offers = "offers";
    public const string Booked = "booked";
    public const string Past = "past";

    public static readonly string[] All = [Waiting, Offers, Booked, Past];
    public static readonly string[] WaitingStatuses = [TripEnquiryStatuses.Asked, TripEnquiryStatuses.Countered];
    public static readonly string[] OffersStatuses = [TripEnquiryStatuses.Offered];
    public static readonly string[] BookedStatuses = [TripEnquiryStatuses.ConfirmedByBoth];

    public static bool IsValid(string? bucket) => bucket != null && Array.IndexOf(All, bucket) >= 0;

    public static string Of(string status, DateOnly? checkOut, DateOnly todayIst)
        => Of(status, TripOwnerBuckets.HasEnded(checkOut, todayIst));

    // ended = a booking whose check-out has passed.
    public static string Of(string status, bool ended) => status switch
    {
        TripEnquiryStatuses.Asked or TripEnquiryStatuses.Countered => Waiting,
        TripEnquiryStatuses.Offered => Offers,
        TripEnquiryStatuses.ConfirmedByBoth => ended ? Past : Booked,
        _ => Past,
    };
}

// "Usually replies within a day": only for hosts with enough history; never a slow flag.
public static class StayReplyRules
{
    public const int MinAsks = 5, MinPercent = 70;

    public static bool RepliesFast(int received, int answeredIn24h)
        => received >= MinAsks && answeredIn24h * 100 >= received * MinPercent;
}

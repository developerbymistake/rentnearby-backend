using System.Globalization;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Core.Models.StayRing;

// Stay enquiry ring (owner order 2026-11-27, contract F:/Dhasu/Buddy_resume/stay_ring/CONTRACT.md): the tested taxi ring, for a Stay
// Owner on a new stay enquiry. Same pipeline (data-only high-priority ring, visible ring end with the ring tag), kind "stay".
public static class RingKinds
{
    public const string Taxi = "taxi";
    public const string Stay = "stay";
}

// The ring push types of every kind: the push pipeline and FcmService pick the ring shapes through these, never by one type.
public static class RingPushTypes
{
    public static bool IsRing(string? type) => type is NotificationTypes.TaxiRequestRing or NotificationTypes.StayEnquiryRing;

    // Visible pushes that replace the ringing notification through the ring tag.
    public static bool IsRingEnd(string? type)
        => type is NotificationTypes.TaxiRequestRingEnded or NotificationTypes.StayEnquiryRingEnded or NotificationTypes.StayEnquiryRingMissed;

    public static string? KindOf(string? type) => type switch
    {
        NotificationTypes.TaxiRequestRing or NotificationTypes.TaxiRequestRingEnded => RingKinds.Taxi,
        NotificationTypes.StayEnquiryRing or NotificationTypes.StayEnquiryRingEnded or NotificationTypes.StayEnquiryRingMissed => RingKinds.Stay,
        _ => null,
    };
}

public static class StayRingPushKinds
{
    public const string Ring = "stay_ring";
    public const string RingTest = "stay_ring_test";
    public const string RingEnd = "stay_ring_end";
    public const string Missed = "stay_ring_missed";
    public const string PayloadVersion = "1";
    public const string RingChannelId = "stay_enquiry_ring_v1";
    public const string UpdatesChannelId = "stay_enquiry_updates_v1";

    public const string RingKeyPrefix = "stayring:";
    public const string MissedKeyPrefix = "stayringmiss:";
    public const string RingEndKeyPrefix = "stayringend:";
    public const string TestKeyPrefix = "stayringtest:";
    public const string TagPrefix = "stay_ring_";

    public static string RingTag(Guid enquiryId) => TagPrefix + enquiryId.ToString("D");
    public static string RingKey(Guid enquiryId) => RingKeyPrefix + enquiryId.ToString("D");
    public static string MissedKey(Guid enquiryId) => MissedKeyPrefix + enquiryId.ToString("D");
    public static string RingEndKey(Guid enquiryId) => RingEndKeyPrefix + enquiryId.ToString("D");
    // The owner may answer the ring on two devices: every answer has its own ring end row.
    public static string AnswerEndKey(Guid enquiryId, Guid unique) => RingEndKeyPrefix + enquiryId.ToString("D") + ":" + unique.ToString("N");

    public static bool TryParseMissedKey(string? key, out Guid enquiryId)
    {
        enquiryId = Guid.Empty;
        return key is not null && key.StartsWith(MissedKeyPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(key[MissedKeyPrefix.Length..], "D", out enquiryId) && enquiryId != Guid.Empty;
    }
}

// Ring end reasons. Answered/Closed come from the enquiry status (any device, any path); Accepted/Declined from the ring buttons
// (POST .../answer), which never change the enquiry.
public static class StayRingEndReasons
{
    public const string Answered = "Answered";
    public const string Closed = "Closed";
    public const string Accepted = "Accepted";
    public const string Declined = "Declined";

    public static readonly string[] Outcomes = [Accepted, Declined];

    public static bool IsOutcome(string? value) => value is Accepted or Declined;

    public static string Body(string reason) => reason switch
    {
        Answered => "You answered this enquiry.",
        Accepted => "You opened this enquiry.",
        Declined => "Ring ignored. The enquiry is still in Enquiries.",
        _ => "This enquiry is closed.",
    };
}

public static class StayRingRules
{
    // Same window, test ring and limits as the taxi ring.
    public const int RingSeconds = TaxiDispatchRules.RingSeconds;
    public const int TestRingSeconds = TaxiDispatchRules.TestRingSeconds;
    public const int TestRingsPerHour = TaxiDispatchRules.TestRingsPerHour;
    public const int AnswersPerHour = 120;
    public const int RingEndTtlSeconds = 60;
    public const int MissedTtlSeconds = 6 * 60 * 60;
    public const int WatchBatch = 500;
    public static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(5);

    // The ring never outlives the enquiry.
    public static DateTime RingExpiresAt(DateTime nowUtc, DateTime enquiryExpiresAtUtc)
    {
        var ring = nowUtc.AddSeconds(RingSeconds);
        return enquiryExpiresAtUtc < ring ? enquiryExpiresAtUtc : ring;
    }

    // Eligibility at the moment of the ring: a Stay Owner whose stay is live (approved, not deleted) and whose switch is on.
    public static bool CanRing(Guid ownerRoleId, string stayStatus, bool stayDeleted, bool ringOn)
        => ownerRoleId == UserRoles.StayOwnerId && stayStatus == StayStatuses.Approved && !stayDeleted && ringOn;

    // Null while the enquiry still waits for the owner (Asked, the host nudge rule of "not answered"); else why the ring ends.
    public static string? EndReasonOf(string? status) => status switch
    {
        TripEnquiryStatuses.Asked => null,
        TripEnquiryStatuses.Offered or TripEnquiryStatuses.Countered or TripEnquiryStatuses.ConfirmedByBoth
            or TripEnquiryStatuses.NotAvailable or TripEnquiryStatuses.Declined or TripEnquiryStatuses.CancelledByOwner
            => StayRingEndReasons.Answered,
        _ => StayRingEndReasons.Closed,
    };
}

// What the ring says about the enquiry. Traveller first name only: never a phone number, a user id or a contact (SPEC use case 14).
public sealed record StayRingSnapshot(
    Guid EnquiryId,
    string? TravellerName,
    string? StayName,
    DateOnly? CheckIn,
    DateOnly? CheckOut,
    int? Adults,
    int? Kids,
    int? Rooms,
    int? PriceTotal,
    string Bucket);

public sealed record StayRingParts(IReadOnlyDictionary<string, string> Data, string Title, string Body);

// Builds the data maps of the stay ring, ring end and missed ring. Every value is a string.
public static class StayRingPayload
{
    public const string RingTitle = "New stay enquiry";
    public const string RingEndTitle = "Enquiry ring ended";
    public const string MissedTitle = "Missed enquiry";
    public const string TestTitle = "Test ring";
    public const string TestLine = "This is how a new enquiry rings";
    // The receiver's own user id, so a device logged into another account stays silent (taxi G9).
    public const string OwnerUserIdKey = "ownerUserId";

    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static Dictionary<string, string> Build(StayRingSnapshot s, DateTime expiresAtUtc, DateTime sentAtUtc, Guid ownerUserId) => new()
    {
        ["notification_type"] = NotificationTypes.ToWireValue(NotificationTypes.StayEnquiryRing),
        ["kind"] = StayRingPushKinds.Ring,
        ["ringKind"] = RingKinds.Stay,
        ["v"] = StayRingPushKinds.PayloadVersion,
        ["enquiryId"] = s.EnquiryId.ToString("D"),
        ["expiresAt"] = TaxiRingPayload.Instant(expiresAtUtc),
        ["sentAt"] = TaxiRingPayload.Instant(sentAtUtc),
        ["title"] = RingTitle,
        ["guestName"] = TripDateText.FirstName(s.TravellerName),
        ["stayName"] = Clean(s.StayName),
        ["checkIn"] = Iso(s.CheckIn),
        ["checkOut"] = Iso(s.CheckOut),
        ["nights"] = TripExecutionRules.Nights(s.CheckIn, s.CheckOut).ToString(Inv),
        ["adults"] = Number(s.Adults),
        ["kids"] = Number(s.Kids),
        ["rooms"] = Number(s.Rooms),
        ["price"] = s.PriceTotal is > 0 ? s.PriceTotal.Value.ToString(Inv) : string.Empty,
        ["line1"] = DatesLine(s.CheckIn, s.CheckOut),
        ["line2"] = Clean(s.StayName),
        ["line3"] = GuestsLine(s.Adults, s.Kids, s.Rooms, s.PriceTotal),
        ["action_route"] = UserNotificationRoutes.StayEnquiries,
        ["action_args_json"] = EnquiryArgs(s.EnquiryId, s.Bucket),
        [OwnerUserIdKey] = ownerUserId.ToString("D"),
    };

    public static StayRingParts BuildEnd(Guid enquiryId, string reason, Guid ownerUserId)
    {
        var data = new Dictionary<string, string>
        {
            ["notification_type"] = NotificationTypes.ToWireValue(NotificationTypes.StayEnquiryRingEnded),
            ["kind"] = StayRingPushKinds.RingEnd,
            ["ringKind"] = RingKinds.Stay,
            ["v"] = StayRingPushKinds.PayloadVersion,
            ["enquiryId"] = enquiryId.ToString("D"),
            ["reason"] = reason,
            ["action_route"] = UserNotificationRoutes.StayEnquiries,
            ["action_args_json"] = EnquiryArgs(enquiryId, null),
            [OwnerUserIdKey] = ownerUserId.ToString("D"),
        };
        return new StayRingParts(data, RingEndTitle, StayRingEndReasons.Body(reason));
    }

    // The enquiry is untouched: the missed ring only points back to it.
    public static StayRingParts BuildMissed(StayRingSnapshot s, Guid ownerUserId)
    {
        var data = new Dictionary<string, string>
        {
            ["notification_type"] = NotificationTypes.ToWireValue(NotificationTypes.StayEnquiryRingMissed),
            ["kind"] = StayRingPushKinds.Missed,
            ["ringKind"] = RingKinds.Stay,
            ["v"] = StayRingPushKinds.PayloadVersion,
            ["enquiryId"] = s.EnquiryId.ToString("D"),
            ["action_route"] = UserNotificationRoutes.StayEnquiries,
            ["action_args_json"] = EnquiryArgs(s.EnquiryId, s.Bucket),
            [OwnerUserIdKey] = ownerUserId.ToString("D"),
        };
        var parts = new[] { TripDateText.FirstName(s.TravellerName), DatesLine(s.CheckIn, s.CheckOut), Clean(s.StayName) }
            .Where(p => p.Length > 0);
        return new StayRingParts(data, MissedTitle, string.Join(" · ", parts) + ". Reply in Enquiries.");
    }

    public static Dictionary<string, string> BuildTest(DateTime nowUtc, DateTime expiresAtUtc, Guid ownerUserId) => new()
    {
        ["notification_type"] = NotificationTypes.ToWireValue(NotificationTypes.StayEnquiryRing),
        ["kind"] = StayRingPushKinds.RingTest,
        ["ringKind"] = RingKinds.Stay,
        ["v"] = StayRingPushKinds.PayloadVersion,
        ["enquiryId"] = "",
        ["expiresAt"] = TaxiRingPayload.Instant(expiresAtUtc),
        ["sentAt"] = TaxiRingPayload.Instant(nowUtc),
        ["title"] = TestTitle,
        ["guestName"] = "",
        ["stayName"] = "",
        ["checkIn"] = "",
        ["checkOut"] = "",
        ["nights"] = "0",
        ["adults"] = "",
        ["kids"] = "",
        ["rooms"] = "",
        ["price"] = "",
        ["line1"] = TestLine,
        ["line2"] = "",
        ["line3"] = "",
        ["action_route"] = UserNotificationRoutes.StayRingReady,
        ["action_args_json"] = "{}",
        [OwnerUserIdKey] = ownerUserId.ToString("D"),
    };

    public static string EnquiryArgs(Guid enquiryId, string? bucket)
        => "{\"enquiryId\":\"" + enquiryId.ToString("D") + "\",\"bucket\":\"" + (bucket ?? TripOwnerBuckets.New) + "\"}";

    // "2 nights · 18 to 20 Dec", "1 night · 18 Dec", "3 nights · 30 Dec to 2 Jan".
    public static string DatesLine(DateOnly? checkIn, DateOnly? checkOut)
    {
        if (checkIn is not { } from) return string.Empty;
        var nights = Math.Max(1, TripExecutionRules.Nights(checkIn, checkOut));
        var to = checkOut is { } c && c > from ? c : from.AddDays(1);
        var head = nights == 1 ? "1 night" : $"{nights} nights";
        string range;
        if (nights == 1) range = $"{from.Day} {from.ToString("MMM", En)}";
        else if (from.Year == to.Year && from.Month == to.Month) range = $"{from.Day} to {to.Day} {to.ToString("MMM", En)}";
        else range = $"{from.Day} {from.ToString("MMM", En)} to {to.Day} {to.ToString("MMM", En)}";
        return $"{head} · {range}";
    }

    // "2 adults, 1 kid · 1 room · Rs 4,500"; parts that are not given are left out.
    public static string GuestsLine(int? adults, int? kids, int? rooms, int? priceTotal)
    {
        var parts = new List<string>(3);
        if (adults is > 0) parts.Add(TripEnquiryText.People(adults, kids));
        if (rooms is > 0) parts.Add(rooms == 1 ? "1 room" : $"{rooms.Value.ToString(Inv)} rooms");
        if (priceTotal is > 0) parts.Add("Rs " + priceTotal.Value.ToString("N0", Inv));
        return string.Join(" · ", parts);
    }

    private static string Clean(string? s) => TripTextSafety.Clean(s, 120);
    private static string Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", Inv) ?? string.Empty;
    private static string Number(int? n) => n is { } v && v >= 0 ? v.ToString(Inv) : string.Empty;
}

public static class StayRingErrorTypes
{
    public const string InvalidOutcome = "InvalidRingOutcome";
    public const string RingOnRequired = "RingOnRequired";
    public const string EnquiryNotFound = "EnquiryNotFound";
    public const string NotEnquiryOwner = "NotEnquiryOwner";
    public const string StayNotFound = "StayNotFound";

    public const string InvalidOutcomeMessage = "Outcome must be Accepted or Declined.";
    public const string RingOnRequiredMessage = "ringOn is required.";
    public const string EnquiryNotFoundMessage = "Enquiry not found.";
    public const string NotEnquiryOwnerMessage = "This enquiry is not for your stay.";
    public const string StayNotFoundMessage = "Stay not found";
}

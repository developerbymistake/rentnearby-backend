using System.Globalization;

namespace RentNearBy.Core.Models.TaxiRequests;

// What a ring says about a request. Never a name, phone or trip name (contract 10.1).
public sealed record TaxiRingSnapshot(
    DateOnly DateFrom,
    DateOnly DateTo,
    string? PickupName,
    IReadOnlyList<string> Cities,
    bool Pickup,
    bool Sightseeing,
    bool Drop,
    int SightseeingDayCount,
    int Seats,
    int Travellers);

public sealed record TaxiRingEndParts(IReadOnlyDictionary<string, string> Data, string Title, string Body);

// Builds the data maps of push_ring.json and push_ring_end.json. Every value is a string.
public static class TaxiRingPayload
{
    public const string RingTitle = "New taxi request";
    public const string RingEndTitle = "Request closed";
    public const int MaxCitiesShown = 3;
    // G9: the receiver's own user id, so a device logged into another account stays silent.
    public const string DriverUserIdKey = "driverUserId";

    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

    public static Dictionary<string, string> Build(Guid offerId, Guid requestId, DateTime expiresAtUtc, DateTime sentAtUtc, int attempt, string ackToken,
        TaxiRingSnapshot snapshot, Guid driverUserId)
    {
        var sections = new List<string>(3);
        if (snapshot.Pickup) sections.Add("pickup");
        if (snapshot.Sightseeing) sections.Add("sightseeing");
        if (snapshot.Drop) sections.Add("drop");

        return new Dictionary<string, string>
        {
            ["notification_type"] = NotificationTypes.ToWireValue(NotificationTypes.TaxiRequestRing),
            ["kind"] = TaxiPushKinds.Ring,
            ["v"] = TaxiPushKinds.PayloadVersion,
            ["offerId"] = offerId.ToString("D"),
            ["requestId"] = requestId.ToString("D"),
            ["expiresAt"] = Instant(expiresAtUtc),
            ["sentAt"] = Instant(sentAtUtc),
            ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
            ["ackToken"] = ackToken,
            ["title"] = RingTitle,
            ["line1"] = DatesLine(snapshot.DateFrom, snapshot.DateTo),
            ["line2"] = RouteLine(snapshot.PickupName, snapshot.Cities),
            ["line3"] = SeatsLine(snapshot.Seats, snapshot.Travellers),
            ["sections"] = string.Join(',', sections),
            ["sightseeingDays"] = snapshot.SightseeingDayCount.ToString(CultureInfo.InvariantCulture),
            ["action_route"] = UserNotificationRoutes.TaxiRequestRing,
            ["action_args_json"] = OfferArgs(offerId),
            [DriverUserIdKey] = driverUserId.ToString("D"),
        };
    }

    // Null when the reason has no ring end push.
    public static TaxiRingEndParts? BuildEnd(Guid offerId, Guid requestId, string reason)
    {
        var body = OfferEndReasons.RingEndBody(reason);
        if (body is null) return null;
        var data = new Dictionary<string, string>
        {
            ["notification_type"] = NotificationTypes.ToWireValue(NotificationTypes.TaxiRequestRingEnded),
            ["kind"] = TaxiPushKinds.RingEnd,
            ["v"] = TaxiPushKinds.PayloadVersion,
            ["offerId"] = offerId.ToString("D"),
            ["requestId"] = requestId.ToString("D"),
            ["reason"] = reason,
            ["action_route"] = UserNotificationRoutes.TaxiRequests,
            ["action_args_json"] = "{}",
        };
        return new TaxiRingEndParts(data, RingEndTitle, body);
    }

    public static string OfferArgs(Guid offerId) => "{\"offerId\":\"" + offerId.ToString("D") + "\"}";

    public static string Instant(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // "4 days · 3 nights · 18 to 21 Dec", "1 day · 18 Dec", "3 days · 2 nights · 30 Dec to 1 Jan".
    public static string DatesLine(DateOnly from, DateOnly to)
    {
        var days = Math.Max(1, to.DayNumber - from.DayNumber + 1);
        var head = days == 1 ? "1 day" : $"{days} days · {days - 1} {(days - 1 == 1 ? "night" : "nights")}";
        string range;
        if (days == 1) range = $"{from.Day} {from.ToString("MMM", En)}";
        else if (from.Year == to.Year && from.Month == to.Month) range = $"{from.Day} to {to.Day} {to.ToString("MMM", En)}";
        else range = $"{from.Day} {from.ToString("MMM", En)} to {to.Day} {to.ToString("MMM", En)}";
        return $"{head} · {range}";
    }

    // "Kathgodam to Nainital, Ranikhet"; after three cities "+N" for the rest.
    public static string RouteLine(string? pickupName, IReadOnlyList<string> cities)
    {
        var shown = string.Join(", ", cities.Take(MaxCitiesShown));
        if (cities.Count > MaxCitiesShown) shown += $" +{cities.Count - MaxCitiesShown}";
        var pickup = string.IsNullOrWhiteSpace(pickupName) ? null : pickupName.Trim();
        if (pickup is null) return shown;
        return shown.Length == 0 ? pickup : $"{pickup} to {shown}";
    }

    // "4 seater · 2 travellers"; seats never fall below the travellers (O2).
    public static string SeatsLine(int seats, int travellers)
        => $"{seats.ToString(CultureInfo.InvariantCulture)} seater · {travellers} {(travellers == 1 ? "traveller" : "travellers")}";
}

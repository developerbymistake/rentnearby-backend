using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

public static class JourneyStatuses
{
    public const string Ready = "Ready";
    public const string DriverUnavailable = "DriverUnavailable";
    public const string NotAsked = "NotAsked";
    public const string Completed = "Completed";

    public static readonly string[] All = [Ready, DriverUnavailable, NotAsked, Completed];
    public const string SqlList = "('Ready','DriverUnavailable','NotAsked','Completed')";
}

public static class JourneyLegStatuses
{
    public const string Free = "Free";
    public const string NotFree = "NotFree";
    public const string NoAnswer = "NoAnswer";
    public const string Unknown = "Unknown";

    public static readonly string[] All = [Free, NotFree, NoAnswer, Unknown];
    public const string SqlList = "('Free','NotFree','NoAnswer','Unknown')";
}

public static class JourneyErrorTypes
{
    public const string JourneyNotFound = "JourneyNotFound";
    public const string JourneyLimitReached = "JourneyLimitReached";
    public const string DriverNotOnLeg = "DriverNotOnLeg";
    public const string ConcurrentUpdate = "CONCURRENT_UPDATE";
}

public static class JourneyRules
{
    public const int MaxLive = 20, MaxLegs = 3, Backups = 2, MaxAskTaxis = 6, MaxAskPerLeg = 3;
    public const int RefreshPerWindow = 30, MaxDaysAhead = 90, MaxPeople = 12, NameMax = 80;
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);

    public static DateOnly TodayIst(DateTime nowUtc) => DateOnly.FromDateTime(nowUtc + IstOffset);

    public static bool IsTravelDate(DateOnly date, DateOnly today)
        => date >= today && date <= today.AddDays(MaxDaysAhead);

    public static string LegStatusOf(TripEnquiry e) => e.Status switch
    {
        TripEnquiryStatuses.FareSent or TripEnquiryStatuses.BookedByTraveller or TripEnquiryStatuses.ConfirmedByTraveller
            or TripEnquiryStatuses.ConfirmedByBoth => JourneyLegStatuses.Free,
        TripEnquiryStatuses.NotAvailable or TripEnquiryStatuses.Declined or TripEnquiryStatuses.Failed => JourneyLegStatuses.NotFree,
        TripEnquiryStatuses.Asked or TripEnquiryStatuses.Offered or TripEnquiryStatuses.Countered => JourneyLegStatuses.NoAnswer,
        _ => JourneyLegStatuses.Unknown,
    };

    // The fare the driver answered with; only meaningful for a Free leg.
    public static int? QuotedFareOf(TripEnquiry e) => LegStatusOf(e) == JourneyLegStatuses.Free ? e.AgreedFare ?? e.DriverFare : null;

    // Never returns Completed: a completed journey is left alone by every caller.
    public static string JourneyStatusOf(IEnumerable<SavedJourneyLeg> legs)
    {
        var list = legs as IReadOnlyCollection<SavedJourneyLeg> ?? legs.ToList();
        if (list.Any(l => !l.DriverAvailable)) return JourneyStatuses.DriverUnavailable;
        if (list.All(l => l.LastEnquiryId == null)) return JourneyStatuses.NotAsked;
        return JourneyStatuses.Ready;
    }

    // Dedupe keys stay under the 80 characters of TripPushLog.DedupeKey (Guid "N" form).
    public static string PausedKey(Guid journeyId, Guid taxiId) => $"jrn:p:{journeyId:N}:{taxiId:N}";
    public static string BackKey(Guid journeyId, Guid taxiId, DateOnly day)
        => $"jrn:b:{journeyId:N}:{taxiId:N}:{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
    public static string EveKey(Guid journeyId) => $"jrn:e:{journeyId:N}";
}

public static partial class JourneyLeadText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [GeneratedRegex(@"^Car (\d) of (\d):", RegexOptions.CultureInvariant)]
    private static partial Regex CarPrefix();

    public static string People(int people) => people == 1 ? "1 person" : $"{people} people";

    // cars > 1: the group needs several vehicles of this driver's size ("5 people, 2 cars").
    public static string Build(
        string fromName, string toName, DateOnly date, int people, int carIndex, int carOf, string? comesFrom, int cars = 1)
    {
        var group = cars > 1 ? $"{People(people)}, {cars} cars" : People(people);
        var trip = $"{fromName} to {toName}, {date.ToString("dd MMM", Inv)}, {group}.";
        if (carOf <= 1) return trip;
        var text = $"Car {carIndex} of {carOf}: {trip}";
        if (!string.IsNullOrWhiteSpace(comesFrom)) text += $" Comes from {comesFrom} with another driver.";
        return text;
    }

    // "Haridwar · car 2 of 2", at most 80 characters (the place name gives way).
    public static string PickupPlace(string fromName, int carIndex, int carOf)
    {
        const int max = TripExecutionRules.MaxTextLen;
        if (carOf <= 1) return Cut(fromName, max);
        var suffix = $" · car {carIndex} of {carOf}";
        return Cut(fromName, max - suffix.Length) + suffix;
    }

    public static bool TryParseCar(string? text, out int carIndex, out int carOf)
    {
        carIndex = 0;
        carOf = 0;
        if (string.IsNullOrEmpty(text)) return false;
        var m = CarPrefix().Match(text);
        if (!m.Success) return false;
        carIndex = int.Parse(m.Groups[1].Value, Inv);
        carOf = int.Parse(m.Groups[2].Value, Inv);
        return true;
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();
}

public static class JourneyNotificationTypes
{
    public const string JourneyDriverPaused = "JourneyDriverPaused";
    public const string JourneyDriverBack = "JourneyDriverBack";
    public const string JourneyDayBefore = "JourneyDayBefore";
}

public static class JourneyNotifications
{
    public const string Route = "/my-journeys";

    public static NotificationEvent DriverPaused(Guid userId, Guid journeyId, string driverName, string from, string to, string? backupName)
        => Make(userId, journeyId, JourneyNotificationTypes.JourneyDriverPaused, "A driver is unavailable",
            string.IsNullOrWhiteSpace(backupName)
                ? $"{driverName} paused his taxi for {from} to {to}. Open the journey to pick another driver."
                : $"{driverName} paused his taxi for {from} to {to}. Backup ready: {backupName}.");

    public static NotificationEvent DriverBack(Guid userId, Guid journeyId, string driverName, string from, string to)
        => Make(userId, journeyId, JourneyNotificationTypes.JourneyDriverBack, "Driver available again",
            $"{driverName} is back for {from} to {to}.");

    public static NotificationEvent DayBefore(Guid userId, Guid journeyId, string from, string to, int notAsked)
        => Make(userId, journeyId, JourneyNotificationTypes.JourneyDayBefore, "Your journey is tomorrow",
            notAsked > 0
                ? $"{from} to {to}. {notAsked} {(notAsked == 1 ? "driver" : "drivers")} not asked yet."
                : $"{from} to {to}. All drivers answered.");

    private static NotificationEvent Make(Guid userId, Guid journeyId, string type, string title, string body) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = type,
        Title = title,
        Body = body,
        ActionRoute = Route,
        ActionArgumentsJson = $"{{\"journeyId\":\"{journeyId}\"}}",
        CreatedAt = DateTime.UtcNow,
    };
}

public static class TaxiRouteGeometryStatuses
{
    public const string Pending = "Pending";
    public const string Ok = "Ok";
    public const string NoRoute = "NoRoute";
    public const string Uncovered = "Uncovered";
    public const string Suspect = "Suspect";
    public const string Failed = "Failed";

    public static readonly string[] All = [Pending, Ok, NoRoute, Uncovered, Suspect, Failed];
    public const string SqlList = "('Pending','Ok','NoRoute','Uncovered','Suspect','Failed')";
}

public static class TaxiRouteGeometryRules
{
    public const int MaxVertices = 200, MaxAttempts = 5, Batch = 20, FailedRetryHours = 24;
    public const double SimplifyMetres = 150;
    public const double SuspectMaxRatio = 2.5, SuspectMinRatio = 0.8;
    public const double EarthRadiusMetres = 6_371_008.8;

    public static string EndsHash(double fromLat, double fromLng, double toLat, double toLng)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{fromLat:F5},{fromLng:F5};{toLat:F5},{toLng:F5}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    // Outside the loaded graph OSRM snaps to the nearest edge: a road length far from the crow-fly length is not this route.
    public static bool IsSuspect(double roadKm, double greatCircleKm)
    {
        if (greatCircleKm <= 0) return true;
        var ratio = roadKm / greatCircleKm;
        return ratio > SuspectMaxRatio || ratio < SuspectMinRatio;
    }

    public static double GreatCircleMetres(double lat1, double lon1, double lat2, double lon2)
    {
        var p1 = lat1 * Math.PI / 180;
        var p2 = lat2 * Math.PI / 180;
        var dp = p2 - p1;
        var dl = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * EarthRadiusMetres * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    // Planar distance from p to segment ab around the segment's latitude, in metres.
    public static double PointToSegmentMetres(double lat, double lon, double aLat, double aLon, double bLat, double bLon)
    {
        var k = Math.Cos((aLat + bLat) / 2 * Math.PI / 180);
        const double mPerDeg = Math.PI / 180 * EarthRadiusMetres;
        double X(double lo) => lo * k * mPerDeg;
        double Y(double la) => la * mPerDeg;
        double px = X(lon), py = Y(lat), ax = X(aLon), ay = Y(aLat), bx = X(bLon), by = Y(bLat);
        double dx = bx - ax, dy = by - ay;
        var len2 = dx * dx + dy * dy;
        var t = len2 <= 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        var cx = ax + t * dx;
        var cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    public static double DistanceToLineMetres(double lat, double lon, IReadOnlyList<(double Lat, double Lon)> line)
    {
        if (line.Count == 0) return double.MaxValue;
        if (line.Count == 1) return GreatCircleMetres(lat, lon, line[0].Lat, line[0].Lon);
        var best = double.MaxValue;
        for (var i = 1; i < line.Count; i++)
        {
            var d = PointToSegmentMetres(lat, lon, line[i - 1].Lat, line[i - 1].Lon, line[i].Lat, line[i].Lon);
            if (d < best) best = d;
        }
        return best;
    }

    // Douglas-Peucker; the tolerance doubles until at most maxVertices stay. Ends are always kept.
    public static List<(double Lat, double Lon)> Simplify(
        IReadOnlyList<(double Lat, double Lon)> line, double toleranceMetres = SimplifyMetres, int maxVertices = MaxVertices)
    {
        if (line.Count <= 2) return line.ToList();
        var tolerance = toleranceMetres;
        List<(double Lat, double Lon)> result;
        do
        {
            result = SimplifyOnce(line, tolerance);
            tolerance *= 2;
        }
        while (result.Count > maxVertices && tolerance < 1_000_000);
        return result;
    }

    private static List<(double Lat, double Lon)> SimplifyOnce(IReadOnlyList<(double Lat, double Lon)> line, double tolerance)
    {
        var keep = new bool[line.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int From, int To)>();
        stack.Push((0, line.Count - 1));
        while (stack.Count > 0)
        {
            var (from, to) = stack.Pop();
            var worst = -1.0;
            var index = -1;
            for (var i = from + 1; i < to; i++)
            {
                var d = PointToSegmentMetres(line[i].Lat, line[i].Lon, line[from].Lat, line[from].Lon, line[to].Lat, line[to].Lon);
                if (d > worst)
                {
                    worst = d;
                    index = i;
                }
            }
            if (index < 0 || worst <= tolerance) continue;
            keep[index] = true;
            stack.Push((from, index));
            stack.Push((index, to));
        }
        var kept = new List<(double Lat, double Lon)>();
        for (var i = 0; i < line.Count; i++)
            if (keep[i]) kept.Add(line[i]);
        return kept;
    }

    public static string ToGeoJson(IReadOnlyList<(double Lat, double Lon)> line)
    {
        var sb = new StringBuilder("{\"type\":\"LineString\",\"coordinates\":[");
        for (var i = 0; i < line.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[').Append(line[i].Lon.ToString("F5", CultureInfo.InvariantCulture))
              .Append(',').Append(line[i].Lat.ToString("F5", CultureInfo.InvariantCulture)).Append(']');
        }
        return sb.Append("]}").ToString();
    }

    // GeoJSON LineString -> (lat, lon) list; empty on anything malformed.
    public static List<(double Lat, double Lon)> ParseLine(string? geoJson)
    {
        var points = new List<(double Lat, double Lon)>();
        if (string.IsNullOrWhiteSpace(geoJson)) return points;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(geoJson);
            if (!doc.RootElement.TryGetProperty("coordinates", out var coords)
                || coords.ValueKind != System.Text.Json.JsonValueKind.Array) return points;
            foreach (var c in coords.EnumerateArray())
            {
                if (c.ValueKind != System.Text.Json.JsonValueKind.Array || c.GetArrayLength() < 2) return new();
                points.Add((c[1].GetDouble(), c[0].GetDouble()));
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return new();
        }
        return points;
    }
}

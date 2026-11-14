using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Core.Models;

public static class TripStatuses
{
    public const string Draft = "Draft";
    public const string Planned = "Planned";
    public const string Completed = "Completed";

    public static readonly string[] All = [Draft, Planned, Completed];
    // Completed is set only by the complete endpoint and the timer.
    public static readonly string[] Savable = [Draft, Planned];
}

public static class TripPlanModes
{
    public const string Auto = "auto";
    public const string Recalculate = "recalculate";

    public static readonly string[] All = [Auto, Recalculate];
}

public static class TripSpotStatuses
{
    public const string Ready = "Ready";
    public const string NotReady = "NotReady";
    public const string Stale = "Stale";
}

public static class TripErrorTypes
{
    public const string TripNotFound = "TripNotFound";
    public const string SpotsUnavailable = "SpotsUnavailable";
    public const string TripPlannerDisabled = "TripPlannerDisabled";
    public const string CityNotFound = "CityNotFound";
    public const string TooManySpots = "TooManySpots";
    public const string TooManySpotsPerDay = "TooManySpotsPerDay";
    public const string TooManySpotsForDays = "TooManySpotsForDays";
    public const string CitiesUnavailable = "TripCitiesUnavailable";
    public const string TripNotPlanned = "TripNotPlanned";
    public const string TripHasLiveEnquiries = "TripHasLiveEnquiries";
}

// Hard caps; TripPlannerOptions can only lower them.
public static class TripLimits
{
    public const int MaxSpots = 60;
    public const int MaxDays = 14;
    public const int MaxLegs = 4;
    public const int MaxSpotsPerDay = 6;
}

public static class TripCacheKeys
{
    public static string Spots(long key) => $"trip:spots2:{key}";
    public static string Stale(long key) => $"trip:spots2:{key}:stale";
    public static string Lock(long key) => $"trip:spots2:{key}:lock";
    public static string Matrix(long key) => $"trip:matrix:{key}";
    public static string MatrixLock(long key) => $"trip:matrix:{key}:lock";
    public static string Supply(long key) => $"trip:supply:{key}";
    public const string CityCatalog = "trip:cities:catalog";
    public const string CityCatalogStale = "trip:cities:catalog:stale";
    public const string CityCatalogLock = "trip:cities:catalog:lock";

    // sha256 of the ordered "lon,lat" list (5 decimals), so the same stops share one geometry.
    public static string Route(IReadOnlyList<(double Lat, double Lon)> points)
    {
        var text = string.Join(';', points.Select(p =>
            $"{p.Lon.ToString("F5", CultureInfo.InvariantCulture)},{p.Lat.ToString("F5", CultureInfo.InvariantCulture)}"));
        return "trip:route:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

public static class RoadUnits
{
    public static int Minutes(double seconds) => (int)Math.Ceiling(seconds / 60.0);
    public static double Km(double metres) => Math.Round(metres / 1000.0, 1);
}

// Identical to Atlas, so the embedded-file fallback yields the same keys.
public static class CityKeys
{
    public const long First = 110000001L;
    public const int Step = 1;

    public static long FromIndex(int i) => First + Step * (long)i;
}

public static class TripJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new EmptyStringNullableGuidConverter() },
    };
}

// Reads "" or whitespace as null (an empty id string is no id); any other text must be a Guid.
public sealed class EmptyStringNullableGuidConverter : JsonConverter<Guid?>
{
    public override bool HandleNull => true;

    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Invalid id");
        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (Guid.TryParseExact(text.Trim(), "D", out var id)) return id;
        throw new JsonException("Invalid id");
    }

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value is { } id) writer.WriteStringValue(id);
        else writer.WriteNullValue();
    }
}

public sealed class AtlasUnavailableException(string message) : Exception(message);

public static class TripPace
{
    public const int Relaxed = 300;
    public const int Normal = 450;
}

// PlanJson v7 (Trip.PlanJson, serialized with TripJson.Options): places per day, cities, settings, steps and row marks.
public sealed class TripPlan
{
    public const int CurrentVersion = 7;
    // A v6 document stays readable (no guide); the first save by a v7 app writes 7.
    public static readonly int[] SupportedVersions = [6, 7];
    public int Version { get; set; } = CurrentVersion;
    // The five city fields always equal Legs[0] after Normalize.
    public long CityKey { get; set; }
    public Guid? DistrictId { get; set; }
    public string CityName { get; set; } = string.Empty;
    public double? CentreLatitude { get; set; }
    public double? CentreLongitude { get; set; }
    // Required on save (1..4); a stored document without legs reads as one leg through Normalize.
    public List<TripCityLeg>? Legs { get; set; }
    public List<TripDay> Days { get; set; } = new();
    public TripSettings Settings { get; set; } = new();
    public TripSteps? Steps { get; set; }
    // The one Local taxi row of the trip: own or skipped.
    public TripRowMark? TaxiMark { get; set; }
    // Problem rows the traveller dismissed (a red night or day reads "not sorted"). Null on a request keeps the stored list.
    public List<Guid>? DismissedEnquiryIds { get; set; }
    // Cursor and answers of the guided Make it easy flow (v7). The shortlist is never stored here (TripShortlistItems).
    public TripGuide? Guide { get; set; }

    // Last DAY of the trip (drives Trips.EndDate, reminders and the list).
    [JsonIgnore]
    public DateOnly? EndDate => Settings?.StartDate?.AddDays(Math.Max(0, (Days?.Count ?? 0) - 1));

    // Every day has a night: check-out is the morning after the last day.
    [JsonIgnore]
    public DateOnly? CheckOutDate => EndDate?.AddDays(1);

    // Coming back without its own date: the last day's evening when he chose it, else the check-out morning.
    [JsonIgnore]
    public DateOnly? ComingBackDate => Steps?.ComingBack?.LeaveOn == TripLeaveOn.LastDayEvening ? EndDate : CheckOutDate;

    [JsonIgnore]
    public List<string> LegCities => Legs is { Count: > 0 } ? Legs.Select(l => l.CityName).ToList() : [CityName];

    // 1 when Legs is null/empty or the day is unknown.
    public int LegIndexOf(int day)
    {
        if (Legs is not { Count: > 0 }) return 1;
        var leg = Days?.FirstOrDefault(d => d != null && d.Day == day)?.Leg ?? 1;
        return Math.Clamp(leg, 1, Legs.Count);
    }

    public TripCityLeg? LegOf(int day)
    {
        if (Legs is not { Count: > 0 }) return null;
        return Legs[LegIndexOf(day) - 1];
    }

    public IEnumerable<TripDay> DaysOf(int legIndex)
        => (Days ?? new List<TripDay>()).Where(d => d != null && LegIndexOf(d.Day) == legIndex);

    // Day numbers; 0 when the leg has no day.
    public int LegStart(int legIndex) => DaysOf(legIndex).Select(d => d.Day).DefaultIfEmpty(0).Min();

    public int LegEnd(int legIndex) => DaysOf(legIndex).Select(d => d.Day).DefaultIfEmpty(0).Max();

    // The first day of a leg 2+ is the travel day (morning check-out, then the road).
    public bool IsTravelDay(int day)
    {
        if (Days?.Any(d => d != null && d.Day == day) != true) return false;
        var leg = LegIndexOf(day);
        return leg >= 2 && LegStart(leg) == day;
    }

    // Pure and idempotent: legs 1..n, every day on a leg (non-decreasing), the five city fields = Legs[0], steps never null.
    public void Normalize(long cityKey = 0, string? cityName = null, double lat = 0, double lon = 0, Guid? districtId = null)
    {
        Legs?.RemoveAll(l => l == null);
        if (Legs is not { Count: > 0 })
        {
            var own = CityKey > 0;
            Legs =
            [
                new TripCityLeg
                {
                    Index = 1,
                    CityKey = own ? CityKey : cityKey,
                    CityName = own && !string.IsNullOrEmpty(CityName) ? CityName : cityName ?? CityName ?? string.Empty,
                    DistrictId = own ? DistrictId ?? districtId : districtId ?? DistrictId,
                    Latitude = own ? CentreLatitude ?? lat : lat,
                    Longitude = own ? CentreLongitude ?? lon : lon,
                },
            ];
        }
        for (var i = 0; i < Legs.Count; i++) Legs[i].Index = i + 1;
        // The row is leg 1 on every save; a district adopted on read reaches the first leg too.
        Legs[0].DistrictId ??= districtId;

        Days ??= new List<TripDay>();
        var previous = 1;
        foreach (var day in Days)
        {
            if (day == null) continue;
            var leg = Math.Clamp(day.Leg ?? previous, 1, Legs.Count);
            if (leg < previous) leg = previous;
            day.Leg = leg;
            previous = leg;
        }

        var first = Legs[0];
        CityKey = first.CityKey;
        CityName = first.CityName;
        DistrictId = first.DistrictId;
        CentreLatitude = first.Latitude;
        CentreLongitude = first.Longitude;

        Steps ??= new TripSteps();
        // A place name is a label, never a blocker: a blank one falls back, a long one is cut.
        if (Steps.StartFrom is { } start) start.Name = Label(start.Name, TripStepRules.NameMax) ?? "My location";
        if (Steps.GoingHomeTo is { } home) home.Name = Label(home.Name, TripStepRules.NameMax) ?? "Drop point";
        foreach (var leg in Legs)
            if (leg.Arrival is { } arrival) arrival.FromName = Label(arrival.FromName, 120) ?? string.Empty;
    }

    private static string? Label(string? raw, int max)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length > max ? text[..max].TrimEnd() : text;
    }
}

// Guided flow state. Step is a step key (TripStepKeys); Choices maps a step key to find | own | skip.
public sealed class TripGuide
{
    public string? Step { get; set; }
    public Dictionary<string, string>? Choices { get; set; }
    public bool? Overview { get; set; }
    // Step keys the traveller postponed with Not now; only the UI cursor depends on them.
    public List<string>? NotNow { get; set; }
}

public static class TripGuideChoices
{
    public const string Find = "find";
    public const string Own = "own";
    public const string Skip = "skip";

    public static readonly string[] All = [Find, Own, Skip];
}

public static class TripStepKeys
{
    public const int MaxChoices = 30;
    public static readonly Regex Pattern = new(@"^(gettingThere|comingBack|taxi|stay:[1-9][0-9]?|between:[2-9][0-9]?)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValid(string? key) => key != null && Pattern.IsMatch(key);
}

public static class TripGuideRules
{
    public const int MaxNotNow = 8;

    // Unknown step or choice values are dropped on read; an empty guide is no guide (toJson writes it only when non-default).
    public static TripGuide? Sanitize(TripGuide? guide)
    {
        if (guide == null) return null;
        var step = TripStepKeys.IsValid(guide.Step) ? guide.Step : null;
        var choices = guide.Choices?
            .Where(c => TripStepKeys.IsValid(c.Key) && Array.IndexOf(TripGuideChoices.All, c.Value) >= 0)
            .Take(TripStepKeys.MaxChoices)
            .ToDictionary(c => c.Key, c => c.Value);
        if (choices is { Count: 0 }) choices = null;
        var overview = guide.Overview == true ? true : (bool?)null;
        var notNow = guide.NotNow?.Where(TripStepKeys.IsValid).Distinct().Take(MaxNotNow).ToList();
        if (notNow is { Count: 0 }) notNow = null;
        if (step == null && choices == null && overview == null && notNow == null) return null;
        return new TripGuide { Step = step, Choices = choices, Overview = overview, NotNow = notNow };
    }

    public static bool IsValid(TripGuide guide)
        => (guide.Step == null || TripStepKeys.IsValid(guide.Step))
           && (guide.Choices == null || (guide.Choices.Count <= TripStepKeys.MaxChoices
               && guide.Choices.All(c => TripStepKeys.IsValid(c.Key) && Array.IndexOf(TripGuideChoices.All, c.Value) >= 0)))
           && (guide.NotNow == null || (guide.NotNow.Count <= MaxNotNow && guide.NotNow.All(TripStepKeys.IsValid)));
}

public static class TripLeaveOn
{
    public const string CheckOut = "checkOut";
    public const string LastDayEvening = "lastDayEvening";

    public static readonly string[] All = [CheckOut, LastDayEvening];
}

// One city of a trip. Index is 1-based and follows the list order.
public sealed class TripCityLeg
{
    public int Index { get; set; }
    public long CityKey { get; set; }
    public string CityName { get; set; } = string.Empty;
    public Guid? DistrictId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    // True = the city has no places yet (stay and taxi only); allowed on legs 2+.
    public bool? NotReady { get; set; }
    // Travel from the previous city; never on leg 1 (that is the trip step Getting there).
    public TripTravel? Arrival { get; set; }
    public TripRowMark? StayMark { get; set; }
    // Legs 2+ only, and only without an Arrival.
    public TripRowMark? TravelMark { get; set; }
}

public sealed class TripRowMark
{
    public string Status { get; set; } = TripRowMarkStatuses.Own;
}

public static class TripRowMarkStatuses
{
    public const string Own = "own";
    public const string Skipped = "skipped";

    public static readonly string[] All = [Own, Skipped];
}

// Journey travel from the previous city: the saved taxi journey that carries him.
public sealed class TripTravel
{
    public string FromName { get; set; } = string.Empty;
    public Guid? JourneyId { get; set; }
    public string? PlanKey { get; set; }
    public int? Cars { get; set; }
    public int? ListedTotal { get; set; }
    public int? RoadMinutes { get; set; }
    // Car numbers (1-based) he arranges himself; each counts as settled.
    public List<int>? OwnCars { get; set; }
}

public sealed class TripDay
{
    public int Day { get; set; }
    // 1-based leg of this day; null on a v3 document (read as the previous day's leg).
    public int? Leg { get; set; }
    // True = no places this day, the traveller's choice.
    public bool? RestDay { get; set; }
    public List<TripSpot> Spots { get; set; } = new();
}

public sealed class TripSpot
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Category { get; set; } = "attraction";
    public int VisitMinutes { get; set; }
    public string? ImageUrl { get; set; }
    // ISO weekdays 1..7 (Mon=1); null = open daily.
    public int[]? ClosedDays { get; set; }
}

public sealed class TripSteps
{
    public TripStartFrom? StartFrom { get; set; }
    // Coming back ends here when set; null = back at StartFrom.
    public TripStartFrom? GoingHomeTo { get; set; }
    public TripStep? GettingThere { get; set; }
    public TripStep? ComingBack { get; set; }
}

public sealed class TripStartFrom
{
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
}

// Absent step or status = notStarted. JourneyId only with chosen|booked (sanitised on save).
public sealed class TripStep
{
    public string? Status { get; set; }
    public Guid? JourneyId { get; set; }
    public DateOnly? Date { get; set; }
    // Coming back only (TripLeaveOn); null = check-out morning.
    public string? LeaveOn { get; set; }
    // Car numbers (1-based) of the step journey he arranges himself.
    public List<int>? OwnCars { get; set; }
}

public static class TripStepStatuses
{
    public const string NotStarted = "notStarted";
    public const string Chosen = "chosen";
    public const string Booked = "booked";
    public const string Own = "own";
    public const string Skipped = "skipped";

    public static readonly string[] All = [NotStarted, Chosen, Booked, Own, Skipped];
}

public static class TripStepRoles
{
    public const string GettingThere = "gettingThere";
    public const string ComingBack = "comingBack";
    // Travel between two cities (legs[k].arrival.journeyId); never a step.
    public const string Segment = "segment";

    public static readonly string[] All = [GettingThere, ComingBack];
}

public sealed class TripSettings
{
    public int DayLimitMinutes { get; set; } = TripPace.Relaxed;
    // Dates and people are optional: a plan works with a day count only.
    public DateOnly? StartDate { get; set; }
    public int? Adults { get; set; }
    public int? Kids { get; set; }
    // 1..TripLimits.MaxDays; null = auto split.
    public int? MaxDays { get; set; }
}

// Redis value of TripCacheKeys.Spots / Stale.
public sealed class CachedCitySpots
{
    public long CityKey { get; set; }
    public string CityName { get; set; } = string.Empty;
    public string Status { get; set; } = TripSpotStatuses.NotReady;
    public DateTime? GeneratedAt { get; set; }
    public DateTime FetchedAt { get; set; }
    public List<TripSpotDto> Spots { get; set; } = new();
}

public sealed record AtlasCityChangedMessage(long CityKey, DateTime ChangedAt);

// Atlas CitySpotsDto.
public sealed class AtlasCitySpots
{
    public long CityKey { get; set; }
    public string CityName { get; set; } = string.Empty;
    public string? DistrictName { get; set; }
    public string? StateName { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public bool TripEnabled { get; set; }
    public bool IsPopularDestination { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? GeneratedAt { get; set; }
    public int SpotCount { get; set; }
    public List<TripSpotDto> Spots { get; set; } = new();
}

// Atlas CityDto.
public sealed class AtlasCity
{
    public long CityKey { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid DistrictId { get; set; }
    public string DistrictName { get; set; } = string.Empty;
    public string StateName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? ImageUrl { get; set; }
    public bool IsTripEnabled { get; set; }
    public bool IsPopularDestination { get; set; }
    public string Status { get; set; } = string.Empty;
    public int SpotCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// Atlas TripCityPageDto.
public sealed class AtlasCityPage
{
    public List<AtlasCity> Items { get; set; } = new();
    public bool HasMore { get; set; }
    public DateTime CatalogUpdatedAt { get; set; }
    public int CatalogCount { get; set; }
}

// Atlas /cities/version.
public sealed class AtlasCatalogVersion
{
    public DateTime UpdatedAt { get; set; }
    public int Count { get; set; }
}

// Redis value of TripCacheKeys.CityCatalog / CityCatalogStale, and the in-process copy.
public sealed class TripCityCatalogSnapshot
{
    // Stored copies with another format (0 = no key, 1 = before the popular flag, 2 = before the cover url) are ignored.
    // The default stays 0 so an old Redis value deserializes as 0; Build stamps CurrentFormat.
    public const int CurrentFormat = 3;
    public int Format { get; set; }
    public string Version { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public int Count { get; set; }
    public DateTime FetchedAt { get; set; }
    // True only on the served-from-stale path, never stored true.
    public bool IsStale { get; set; }
    // Ordered by Name (OrdinalIgnoreCase), then CityKey.
    public List<TripCityInfo> Cities { get; set; } = new();

    // "<unix ms of UpdatedAt>-<count>"; the app compares it as an opaque string.
    public static string VersionOf(DateTime updatedAtUtc, int count)
        => $"{new DateTimeOffset(DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()}-{count}";
}

public sealed class TripCityInfo
{
    public long CityKey { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DistrictName { get; set; } = string.Empty;
    public string StateName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? ImageUrl { get; set; }
    // Cloudinary thumbnail of ImageUrl, else of the best spot image; null when the city has none. Filled by the catalog.
    public string? CoverUrl { get; set; }
    public bool IsPopularDestination { get; set; }
    public string Status { get; set; } = TripSpotStatuses.NotReady;
    public int SpotCount { get; set; }
}

// n x n, indexed like the spot list; null cells become estimates.
public sealed record RoadMatrix(int[,]? Minutes, double[,]? Km, bool[,] IsEstimate);

// Raw OSRM table: sources x destinations, seconds and metres; null = no route.
public sealed record RoadTable(double?[,] Seconds, double?[,] Metres);

// Road minutes between chosen spots out of the cached city matrix; null = no route or id not in the matrix.
public static class TripSpotMinutes
{
    public const int MaxIds = 25;

    public static int?[][] Build(CityRoadMatrix matrix, IReadOnlyList<string> spotIds)
    {
        var n = matrix.Ids.Count;
        var index = new Dictionary<string, int>(n, StringComparer.Ordinal);
        for (var i = 0; i < n; i++) index.TryAdd(matrix.Ids[i], i);

        var rows = spotIds.Select(id => index.TryGetValue(id, out var r) ? r : -1).ToArray();
        var result = new int?[spotIds.Count][];
        for (var i = 0; i < rows.Length; i++)
        {
            result[i] = new int?[rows.Length];
            for (var j = 0; j < rows.Length; j++)
            {
                if (rows[i] < 0 || rows[j] < 0) continue;
                if (rows[i] == rows[j])
                {
                    result[i][j] = 0;
                    continue;
                }
                var cell = rows[i] * n + rows[j];
                if (matrix.Sec[cell] < 0) continue;
                result[i][j] = RoadUnits.Minutes(matrix.Sec[cell]);
            }
        }
        return result;
    }
}

// Redis value of TripCacheKeys.Matrix: n x n row-major over Ids, -1 = no route.
// Sec is whole seconds rounded up; Metres is kept at the 0.1 km RoadMatrix carries, so a slice equals a direct table.
public sealed class CityRoadMatrix
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("v")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("ids")] public List<string> Ids { get; set; } = new();
    [JsonPropertyName("sec")] public int[] Sec { get; set; } = [];
    [JsonPropertyName("km")] public int[] Metres { get; set; } = [];

    public static int StoredSeconds(double seconds) => (int)Math.Ceiling(seconds);
    public static int StoredMetres(double metres) => (int)Math.Round(RoadUnits.Km(metres) * 1000);

    [JsonIgnore]
    public bool IsValid
        => Version == CurrentVersion && Sec.Length == Ids.Count * Ids.Count && Metres.Length == Sec.Length;

    // null when any id is not in the matrix.
    public RoadMatrix? Slice(IReadOnlyList<string> spotIds)
    {
        if (!IsValid) return null;
        var n = Ids.Count;
        var index = new Dictionary<string, int>(n, StringComparer.Ordinal);
        for (var i = 0; i < n; i++) index.TryAdd(Ids[i], i);

        var rows = new int[spotIds.Count];
        for (var i = 0; i < spotIds.Count; i++)
            if (!index.TryGetValue(spotIds[i], out rows[i])) return null;

        var count = spotIds.Count;
        var minutes = new int[count, count];
        var km = new double[count, count];
        var isEstimate = new bool[count, count];
        for (var i = 0; i < count; i++)
        for (var j = 0; j < count; j++)
        {
            if (i == j) continue;
            var cell = rows[i] * n + rows[j];
            if (Sec[cell] < 0 || Metres[cell] < 0)
            {
                isEstimate[i, j] = true;
                continue;
            }
            minutes[i, j] = RoadUnits.Minutes(Sec[cell]);
            km[i, j] = RoadUnits.Km(Metres[cell]);
        }
        return new RoadMatrix(minutes, km, isEstimate);
    }
}

// Geometry = GeoJSON LineString.
public sealed record RoadRoute(int Minutes, double Km, JsonElement Geometry);

// FirstDayUsedMinutes: road time already spent on day 1 (a travel day into the city).
public sealed record SplitInput(IReadOnlyList<TripSpot> Spots, double CentreLat, double CentreLon, int DayLimitMinutes, DateOnly? StartDate, int MaxDays, int FirstDayUsedMinutes = 0, int MaxSpotsPerDay = TripLimits.MaxSpotsPerDay);

public sealed record SplitLeg(string FromId, string ToId, int Minutes, double Km, bool IsEstimate);

public sealed record SplitDay(int Day, List<TripSpot> Spots, int VisitMinutes, int DriveMinutes, double Km, bool IsEstimate, bool OverLimit, List<SplitLeg> Legs)
{
    public int TotalMinutes => VisitMinutes + DriveMinutes;
}

public sealed record SplitWarning(string Code, string Message, string? SpotId, int? Day);

public sealed record SplitResult(List<SplitDay> Days, List<SplitWarning> Warnings);

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public const string CitiesUnavailable = "TripCitiesUnavailable";
}

// Hard caps; TripPlannerOptions can only lower them.
public static class TripLimits
{
    public const int MaxSpots = 40;
    public const int MaxDays = 14;
}

public static class TripCacheKeys
{
    public static string Spots(long key) => $"trip:spots:{key}";
    public static string Stale(long key) => $"trip:spots:{key}:stale";
    public static string Lock(long key) => $"trip:spots:{key}:lock";
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

// Reads "" or whitespace as null (an own stay or taxi may carry an empty id); any other text must be a Guid.
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

public static class TripNeeds
{
    public const string NoStairs = "no_stairs";
    public const string KidsSafe = "kids_safe";
    public const string VegFood = "veg_food";

    public static readonly string[] All = [NoStairs, KidsSafe, VegFood];
}

public static class TripPace
{
    public const int Relaxed = 300;
    public const int Normal = 450;
}

// PlanJson v3 (Trip.PlanJson, serialized with TripJson.Options); v2 documents read as v3 with the new fields null.
public sealed class TripPlan
{
    public const int CurrentVersion = 3;
    public static readonly int[] SupportedVersions = [2, 3];
    public int Version { get; set; } = CurrentVersion;
    public long CityKey { get; set; }
    public Guid? DistrictId { get; set; }
    public string CityName { get; set; } = string.Empty;
    public double? CentreLatitude { get; set; }
    public double? CentreLongitude { get; set; }
    public List<TripDay> Days { get; set; } = new();
    public TripSettings Settings { get; set; } = new();

    // Last DAY of the trip (drives Trips.EndDate, reminders and the list).
    [JsonIgnore]
    public DateOnly? EndDate => Settings?.StartDate?.AddDays(Math.Max(0, (Days?.Count ?? 0) - 1));

    // Every day has a night: check-out is the morning after the last day.
    [JsonIgnore]
    public DateOnly? CheckOutDate => EndDate?.AddDays(1);
}

public sealed class TripDay
{
    public int Day { get; set; }
    public List<TripSpot> Spots { get; set; } = new();
    public TripTaxiChoice? Taxi { get; set; }
    public TripStayChoice? Stay { get; set; }
    public List<TripStayShortlistItem>? StayShortlist { get; set; }
    public List<TripTaxiShortlistItem>? TaxiShortlist { get; set; }
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

public sealed class TripTaxiChoice
{
    // Null only for an own booking (IsOwn).
    public Guid? TaxiId { get; set; }
    public string Name { get; set; } = "";
    // Per-day rupees: the day rate when selected, the booked share round(Booking.AgreedFare / Booking.Days.Length), or the own taxi fare.
    public int? Fare { get; set; }
    public string? VehicleTypeName { get; set; }
    public bool? IsOwn { get; set; }
    public string? OwnPhone { get; set; }
    public string? Note { get; set; }
    public bool? WholeTrip { get; set; }
    public TripTaxiBooking? Booking { get; set; }
}

// "I booked": the same copy sits on every day listed in Days.
public sealed class TripTaxiBooking
{
    // Total for all booked days, rupees.
    public int AgreedFare { get; set; }
    public bool TollExtra { get; set; }
    public string? PickupTime { get; set; }
    public string? PickupPlace { get; set; }
    public int[] Days { get; set; } = [];
    public string? Note { get; set; }
    public DateTime? BookedAt { get; set; }
}

public sealed class TripStayChoice
{
    // Null only for an own booking (IsOwn) or a skipped night (Skipped).
    public Guid? StayId { get; set; }
    public string Name { get; set; } = "";
    public int? PricePerNight { get; set; }
    public int? SameAsDay { get; set; }
    // Both or neither.
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool? IsOwn { get; set; }
    public string? OwnPhone { get; set; }
    public string? Note { get; set; }
    // No stay tonight (travelling).
    public bool? Skipped { get; set; }
}

public sealed class TripStayShortlistItem
{
    public Guid StayId { get; set; }
    public string Name { get; set; } = "";
    public int? PricePerNight { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? DistanceKm { get; set; }
    public int? SameAsDay { get; set; }
}

public sealed class TripTaxiShortlistItem
{
    public Guid TaxiId { get; set; }
    public string Name { get; set; } = "";
    public string? VehicleTypeName { get; set; }
    public short? Seats { get; set; }
    public int? LocalDayRate { get; set; }
    public bool? WholeTrip { get; set; }
}

public sealed class TripSettings
{
    public int DayLimitMinutes { get; set; } = TripPace.Normal;
    public DateOnly? StartDate { get; set; }
    public int? Adults { get; set; }
    public int? Kids { get; set; }
    public int? Rooms { get; set; }
    public string[]? Needs { get; set; }
    // "HH:mm", 24 h.
    public string? PickupTime { get; set; }
    public string? PickupPlace { get; set; }
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
    public string? ImageCredit { get; set; }
    public string? ImageLicense { get; set; }
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
    // Stored copies with another format (0 = no key, 1 = before the popular flag) are ignored.
    // The default stays 0 so an old Redis value deserializes as 0; Build stamps CurrentFormat.
    public const int CurrentFormat = 2;
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
    public bool IsPopularDestination { get; set; }
    public string Status { get; set; } = TripSpotStatuses.NotReady;
    public int SpotCount { get; set; }
}

// n x n, indexed like the spot list; null cells become estimates.
public sealed record RoadMatrix(int[,]? Minutes, double[,]? Km, bool[,] IsEstimate);

// Raw OSRM table: sources x destinations, seconds and metres; null = no route.
public sealed record RoadTable(double?[,] Seconds, double?[,] Metres);

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

public sealed record SplitInput(IReadOnlyList<TripSpot> Spots, double CentreLat, double CentreLon, int DayLimitMinutes, DateOnly? StartDate, int MaxDays);

public sealed record SplitLeg(string FromId, string ToId, int Minutes, double Km, bool IsEstimate);

public sealed record SplitDay(int Day, List<TripSpot> Spots, int VisitMinutes, int DriveMinutes, double Km, bool IsEstimate, bool OverLimit, List<SplitLeg> Legs)
{
    public int TotalMinutes => VisitMinutes + DriveMinutes;
}

public sealed record SplitWarning(string Code, string Message, string? SpotId, int? Day);

public sealed record SplitResult(List<SplitDay> Days, List<SplitWarning> Warnings);

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
    public const int MaxSpots = 60;
    public const int MaxDays = 14;
    public const int MaxLegs = 4;
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

// PlanJson v5 (Trip.PlanJson, serialized with TripJson.Options); v2/v3/v4 documents read with the new fields null
// and become one leg through Normalize.
public sealed class TripPlan
{
    public const int CurrentVersion = 5;
    public static readonly int[] SupportedVersions = [2, 3, 4, 5];
    public int Version { get; set; } = CurrentVersion;
    // The five city fields always equal Legs[0] after Normalize.
    public long CityKey { get; set; }
    public Guid? DistrictId { get; set; }
    public string CityName { get; set; } = string.Empty;
    public double? CentreLatitude { get; set; }
    public double? CentreLongitude { get; set; }
    // Null = a v3 document or an app that does not know cities; Normalize makes it one leg.
    public List<TripCityLeg>? Legs { get; set; }
    public List<TripDay> Days { get; set; } = new();
    public TripSettings Settings { get; set; } = new();
    // Null = written by an app that does not know steps: the server keeps the stored copy (TripStepsMerge).
    public TripSteps? Steps { get; set; }
    // Problem rows the traveller dismissed (a red night or day reads "not sorted"). Null on a request keeps the stored list.
    public List<Guid>? DismissedEnquiryIds { get; set; }

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

    // Pure and idempotent: legs 1..n, every day on a leg (non-decreasing), the five city fields = Legs[0].
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

        // The app never decides a step is not needed: an old auto value is a step not started.
        foreach (var step in new[] { Steps?.GettingThere, Steps?.ComingBack })
            if (step?.Status == TripStepStatuses.NotNeeded) step.Status = TripStepStatuses.NotStarted;
    }
}

public static class TripLeaveOn
{
    public const string CheckOut = "checkOut";
    public const string LastDayEvening = "lastDayEvening";

    public static readonly string[] All = [CheckOut, LastDayEvening];
}

// What an app older than PlanJson v5 cannot send: it keeps the stored rest days, leave-on, own cars, own step travel and going-home place.
public static class TripPlanMerge
{
    public static void KeepStored(TripPlan plan, TripPlan? stored, int requestVersion)
    {
        plan.DismissedEnquiryIds ??= stored?.DismissedEnquiryIds;
        if (stored == null || requestVersion >= 5) return;

        if (plan.Steps != null && stored.Steps?.GoingHomeTo != null) plan.Steps.GoingHomeTo ??= stored.Steps.GoingHomeTo;

        foreach (var day in plan.Days)
            if (day != null && day.RestDay == null)
                day.RestDay = stored.Days.FirstOrDefault(d => d != null && d.Day == day.Day)?.RestDay;

        KeepStep(plan.Steps?.GettingThere, stored.Steps?.GettingThere);
        KeepStep(plan.Steps?.ComingBack, stored.Steps?.ComingBack);
        if (plan.Steps?.ComingBack is { } back && stored.Steps?.ComingBack is { } storedBack) back.LeaveOn ??= storedBack.LeaveOn;

        var storedLegs = stored.Legs ?? new List<TripCityLeg>();
        foreach (var leg in plan.Legs ?? new List<TripCityLeg>())
        {
            if (leg?.Arrival is not { JourneyId: { } id } arrival || arrival.OwnCars != null) continue;
            arrival.OwnCars = storedLegs.FirstOrDefault(l => l?.Arrival?.JourneyId == id)?.Arrival?.OwnCars;
        }
    }

    private static void KeepStep(TripStep? step, TripStep? stored)
    {
        if (step == null || stored == null) return;
        if (step.Status == TripStepStatuses.Own && stored.Status == TripStepStatuses.Own
            && step.How == null && step.OwnName == null && step.OwnPhone == null && step.LeavesAt == null)
        {
            step.How = stored.How;
            step.OwnName = stored.OwnName;
            step.OwnPhone = stored.OwnPhone;
            step.LeavesAt = stored.LeavesAt;
        }
        if (step.OwnCars != null || step.JourneyId == null || step.JourneyId != stored.JourneyId) return;
        step.OwnCars = stored.OwnCars;
    }
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
}

public sealed class TripTravel
{
    public string Mode { get; set; } = TripTravelModes.Own;
    public string FromName { get; set; } = string.Empty;
    public string? How { get; set; }
    public string? OwnName { get; set; }
    public string? OwnPhone { get; set; }
    // "HH:mm", 24 h.
    public string? LeavesAt { get; set; }
    public int? RoadMinutes { get; set; }
    public string? Note { get; set; }
    // Journey mode only: the saved taxi journey that carries him from the previous city.
    public Guid? JourneyId { get; set; }
    public string? PlanKey { get; set; }
    public int? Cars { get; set; }
    public int? ListedTotal { get; set; }
    // Own mode: the train/bus hub he reaches when no driver goes the whole road.
    public string? HubName { get; set; }
    // Journey mode: car numbers (1-based) he arranges himself; each counts as settled.
    public List<int>? OwnCars { get; set; }
}

public static class TripTravelModes
{
    public const string Own = "own";
    public const string Journey = "journey";

    public static readonly string[] All = [Own, Journey];
}

public static class TripTravelHows
{
    public const string Car = "car";
    public const string Bus = "bus";
    public const string Train = "train";
    public const string Other = "other";

    public static readonly string[] All = [Car, Bus, Train, Other];
}

public static class TripTravelRoad
{
    public const int HalfDay = 240;
    public const int WholeDay = 450;

    public static readonly int[] All = [HalfDay, WholeDay];
}

public sealed class TripDay
{
    public int Day { get; set; }
    // 1-based leg of this day; null on a v3 document (read as the previous day's leg).
    public int? Leg { get; set; }
    // True = no places this day, the traveller's choice.
    public bool? RestDay { get; set; }
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
    // Null only for an own booking (IsOwn) or a day without taxi (Skipped).
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
    // No taxi this day.
    public bool? Skipped { get; set; }
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
    public string? Note { get; set; }
    public DateOnly? Date { get; set; }
    // Coming back only (TripLeaveOn); null = check-out morning.
    public string? LeaveOn { get; set; }
    // Car numbers (1-based) of the step journey he arranges himself.
    public List<int>? OwnCars { get; set; }
    // Own travel (Status own only), same shape as TripTravel: TripTravelHows, name or number, phone, "HH:mm".
    public string? How { get; set; }
    public string? OwnName { get; set; }
    public string? OwnPhone { get; set; }
    public string? LeavesAt { get; set; }
}

public static class TripStepStatuses
{
    public const string NotStarted = "notStarted";
    public const string Chosen = "chosen";
    public const string Booked = "booked";
    public const string Own = "own";
    public const string Skipped = "skipped";
    public const string NotNeeded = "notNeeded";

    public static readonly string[] All = [NotStarted, Chosen, Booked, Own, Skipped, NotNeeded];
}

public static class TripStepRoles
{
    public const string GettingThere = "gettingThere";
    public const string ComingBack = "comingBack";
    // Travel between two cities (legs[k].arrival.journeyId); never a step.
    public const string Segment = "segment";

    public static readonly string[] All = [GettingThere, ComingBack];
}

public static class TripStepDisplays
{
    public const string NotStarted = TripStepStatuses.NotStarted;
    public const string Asked = "asked";
    public const string Chosen = TripStepStatuses.Chosen;
    public const string Booked = TripStepStatuses.Booked;
    public const string Own = TripStepStatuses.Own;
    public const string Skipped = TripStepStatuses.Skipped;
    public const string NotNeeded = TripStepStatuses.NotNeeded;

    public static readonly string[] All = [NotStarted, Asked, Chosen, Booked, Own, Skipped, NotNeeded];
}

public static class TripStepsMerge
{
    // Requested null (an older app) keeps the stored steps; any object, {} included, is authoritative.
    public static TripSteps? Resolve(TripSteps? stored, TripSteps? requested) => requested ?? stored;
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
    // Skipped night that moved Coming back to the last evening; Undo restores check-out.
    public bool? LeaveMoved { get; set; }
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

// FirstDayUsedMinutes: road time already spent on day 1 (a travel day into the city).
public sealed record SplitInput(IReadOnlyList<TripSpot> Spots, double CentreLat, double CentreLon, int DayLimitMinutes, DateOnly? StartDate, int MaxDays, int FirstDayUsedMinutes = 0);

public sealed record SplitLeg(string FromId, string ToId, int Minutes, double Km, bool IsEstimate);

public sealed record SplitDay(int Day, List<TripSpot> Spots, int VisitMinutes, int DriveMinutes, double Km, bool IsEstimate, bool OverLimit, List<SplitLeg> Legs)
{
    public int TotalMinutes => VisitMinutes + DriveMinutes;
}

public sealed record SplitWarning(string Code, string Message, string? SpotId, int? Day);

public sealed record SplitResult(List<SplitDay> Days, List<SplitWarning> Warnings);

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RentNearBy.Core.Utils;

namespace RentNearBy.Core.Models;

public sealed class TaxiWaysOptions
{
    public const string Section = "TaxiWays";

    public double SamePlaceMeters { get; set; } = 5000;
    public double AlongRoadMeters { get; set; } = 3000;
    public int MaxPassPlaces { get; set; } = 10;
    public double SimplifyMeters { get; set; } = 150;
    public int MaxVertices { get; set; } = 200;
    public double GeometryEndToleranceMeters { get; set; } = 5000;
    public double MyLocationNearEndMeters { get; set; } = 15000;
    public double BasedHereMeters { get; set; } = 15000;

    public int MaxCars { get; set; } = 3;
    public int MaxPlans { get; set; } = 5;
    public int TopDrivers { get; set; } = 10;
    public int TopDriversPerLeg { get; set; } = 3;
    public int DetailDriversPerLeg { get; set; } = 10;
    public int MaxExpansions { get; set; } = 20_000;

    public int PlanCacheMinutes { get; set; } = 10;
    public int DestCacheMinutes { get; set; } = 5;
    public int MemoryCacheSeconds { get; set; } = 60;
    public int DistrictCacheDays { get; set; } = 7;
    public int RebuildSafetyMinutes { get; set; } = 10;

    public int SearchPerMinute { get; set; } = 60;
    public double ReachMaxKm { get; set; } = 400;
    public double StayRadiusKm { get; set; } = 15;
    public int MaxStays { get; set; } = 6;
    public int MaxLocalTaxis { get; set; } = 10;
    public int MaxDaysAhead { get; set; } = 365;
    public int MaxPeople { get; set; } = 12;
    public int DefaultPeople { get; set; } = 2;
    public int LegPageSize { get; set; } = 8;
    public int LegPageSizeMax { get; set; } = 20;
}

public static class TaxiWaysErrorTypes
{
    public const string TaxiWaysUnavailable = "TaxiWaysUnavailable";
    public const string PlanNotFound = "PlanNotFound";
    public const string LegNotFound = "LegNotFound";
    public const string InvalidDate = "InvalidDate";
}

public static class TaxiWaysSorts
{
    public const string BestFit = "bestFit";
    public const string LowestPrice = "lowestPrice";

    public static bool IsValid(string? sort) => sort is BestFit or LowestPrice;
}

public static class TaxiWaysBestKinds
{
    public const string Driver = "driver";
    public const string Plan = "plan";
}

public sealed class TaxiWaysUnavailableException() : Exception("Ways to go are not available right now");

public sealed record TaxiWaysQuery(
    double FromLat, double FromLng, double ToLat, double ToLng, int People,
    bool FromMyLocation = false, string? FromName = null, string? ToName = null);

public sealed record LegDriverQuery(
    int People, short? MinSeats = null, string? Category = null, bool RepliesInApp = false,
    string Sort = TaxiWaysSorts.BestFit, int Page = 1, int PageSize = 8);

public readonly record struct TaxiWaysPoint(double Lat, double Lng);

// One public route with its taxi, as loaded per rebuild. Geometry only when its status is Ok.
public sealed class TaxiWaysSourceRow
{
    public Guid RouteId { get; set; }
    public Guid TaxiId { get; set; }
    public Guid UserId { get; set; }
    public string? DriverName { get; set; }
    public string? VehicleTypeName { get; set; }
    public string? VehicleCategory { get; set; }
    public short? Seats { get; set; }
    public string? PlateDisplay { get; set; }
    public Guid? CityId { get; set; }
    public string? CityName { get; set; }
    public double? BaseLat { get; set; }
    public double? BaseLng { get; set; }
    public int? LocalDayRate { get; set; }
    public short PriorityRank { get; set; }
    public bool IsFeatured { get; set; }
    public string TripReplyLabel { get; set; } = TaxiTripReplyLabels.New;
    public string FromName { get; set; } = string.Empty;
    public double FromLat { get; set; }
    public double FromLng { get; set; }
    public string ToName { get; set; } = string.Empty;
    public double ToLat { get; set; }
    public double ToLng { get; set; }
    public int Fare { get; set; }
    public string TripType { get; set; } = TaxiTripTypes.OneWay;
    public string? GeometryJson { get; set; }
    public double? DistanceKm { get; set; }
    public int? Minutes { get; set; }
}

public sealed class TaxiWaysPlace
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public double Lat { get; init; }
    public double Lng { get; init; }
    public long? CityKey { get; init; }
    public Guid? DistrictId { get; set; }
}

public sealed class TaxiWaysDriver
{
    public Guid TaxiId { get; init; }
    public Guid UserId { get; init; }
    public string? DriverName { get; init; }
    public string? VehicleTypeName { get; init; }
    public string? VehicleCategory { get; init; }
    public short? Seats { get; init; }
    public string? PlateDisplay { get; init; }
    public Guid? CityId { get; init; }
    public string? CityName { get; init; }
    public double? BaseLat { get; init; }
    public double? BaseLng { get; init; }
    public int? LocalDayRate { get; init; }
    public short PriorityRank { get; init; }
    public bool IsFeatured { get; init; }
    public string TripReplyLabel { get; init; } = TaxiTripReplyLabels.New;
}

public sealed record TaxiWaysPass(string PlaceKey, double Fraction, double DistanceMeters);

// A route as the network sees it. Line is empty when there is no road geometry.
public sealed class TaxiWaysRouteLine
{
    public Guid RouteId { get; init; }
    public Guid TaxiId { get; init; }
    public string FromName { get; init; } = string.Empty;
    public string ToName { get; init; } = string.Empty;
    public string FromPlace { get; init; } = string.Empty;
    public string ToPlace { get; init; } = string.Empty;
    // Every place within SamePlaceMeters of the route's own end point, the nearest (FromPlace / ToPlace) first.
    public IReadOnlyList<string> FromPlaces { get; init; } = [];
    public IReadOnlyList<string> ToPlaces { get; init; } = [];
    public int Fare { get; init; }
    public string TripType { get; init; } = TaxiTripTypes.OneWay;
    public double? DistanceKm { get; init; }
    public int? Minutes { get; init; }
    public IReadOnlyList<TaxiWaysPoint> Line { get; init; } = [];
    public double[] Cumulative { get; init; } = [];
    // Interior places only, ordered by Fraction (0 = FromPlace, 1 = ToPlace).
    public IReadOnlyList<TaxiWaysPass> Passes { get; init; } = [];

    public bool HasLine => Line.Count >= 2;
}

// Undirected: FromPlace/ToPlace follow the route's stored direction (FromFraction < ToFraction).
public sealed class TaxiWaysLink
{
    public Guid RouteId { get; init; }
    public Guid TaxiId { get; init; }
    public string FromPlace { get; init; } = string.Empty;
    public string ToPlace { get; init; } = string.Empty;
    public int Fare { get; init; }
    public string TripType { get; init; } = TaxiTripTypes.OneWay;
    public bool Partial { get; init; }
    public double FromFraction { get; init; }
    public double ToFraction { get; init; } = 1;
    public IReadOnlyList<string> Via { get; init; } = [];
    // Virtual anchors only: the named place where the traveller meets this driver.
    public string? MeetAtPlace { get; init; }

    public double Span => TaxiFare.SpanOf(FromFraction, ToFraction);
    // The price of the ridden part: the listed fare for a whole route, the road-length share of it for a part.
    public int EstimatedFare => Partial ? TaxiFare.ForSpan(Fare, Span) : Fare;
    public bool FareEstimated => Partial && TaxiFare.IsPart(Span);

    public string OtherEnd(string key) => FromPlace == key ? ToPlace : FromPlace;
    public bool Touches(string key) => FromPlace == key || ToPlace == key;
}

public sealed class TaxiWaysNetworkSnapshot
{
    public string Version { get; init; } = string.Empty;
    public DateTime BuiltAt { get; init; }
    public Dictionary<string, TaxiWaysPlace> Places { get; init; } = new();
    public List<TaxiWaysLink> Links { get; init; } = new();
    public Dictionary<string, List<int>> Adjacency { get; init; } = new();
    public Dictionary<string, List<int>> Pairs { get; init; } = new();
    public Dictionary<Guid, TaxiWaysDriver> Drivers { get; init; } = new();
    public Dictionary<Guid, TaxiWaysRouteLine> Routes { get; init; } = new();
    public Dictionary<long, List<string>> PlaceGrid { get; init; } = new();
    public Dictionary<long, List<Guid>> RouteGrid { get; init; } = new();

    public IEnumerable<TaxiWaysLink> LinksAt(string placeKey) =>
        Adjacency.TryGetValue(placeKey, out var list) ? list.Select(i => Links[i]) : [];

    public IEnumerable<TaxiWaysLink> LinksBetweenPlaces(string a, string b) =>
        Pairs.TryGetValue(TaxiWaysKeys.PairKey(a, b), out var list) ? list.Select(i => Links[i]) : [];
}

// A resolved traveller end: a snapshot place, or a virtual point with its own links to places.
public sealed class TaxiWaysAnchor
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public double Lat { get; init; }
    public double Lng { get; init; }
    public TaxiWaysPlace? Place { get; init; }
    public bool MyLocation { get; init; }
    public List<TaxiWaysLink> Links { get; init; } = new();
    // Geometry hits: route id -> fraction along it.
    public Dictionary<Guid, double> RouteFractions { get; init; } = new();

    public bool Resolved => Place != null;
}

public sealed record TaxiWaysCandidate(TaxiWaysDriver Driver, TaxiWaysLink Link, bool Reversed, bool BasedHere);

public sealed record TaxiWaysStop(string Key, string Name, double Lat, double Lng);

public sealed class TaxiWaysLegResult
{
    public int Index { get; init; }
    public string LegKey { get; init; } = string.Empty;
    public TaxiWaysStop From { get; init; } = null!;
    public TaxiWaysStop To { get; init; } = null!;
    public string? MeetingPlace { get; set; }
    // All taxis on this stretch, one row per taxi, ranked.
    public List<TaxiWaysCandidate> Drivers { get; init; } = new();

    // People in the group; 0 = unknown (one car).
    public int People { get; init; }
    // Ranked drivers the group may be split over (the plan detail shows this many).
    public int CoverPool { get; init; } = 10;
    // Road minutes of the top driver's ridden part; null when the route has no minutes.
    public int? Minutes { get; init; }

    private SeatCoverResult? _cover;

    public TaxiWaysCandidate Top => Drivers[0];
    // The distinct cars that carry the group at the least cost (TaxiSeatCover).
    public SeatCoverResult Cover => _cover ??= TaxiSeatCover.Solve(
        Drivers.Take(Math.Max(1, CoverPool)).Select((c, i) => new SeatOption(i, c.Driver.Seats, c.Link.EstimatedFare)).ToList(), People);
    public IReadOnlyList<TaxiWaysCandidate> CoverDrivers => Cover.Chosen.Select(i => Drivers[i]).ToList();
    public int CarsNeeded => Cover.Cars;
    // The fare of the whole stretch for the group: the sum over the chosen cars.
    public int ListedFare => Cover.Fare;
    public string? SeatsNote => Cover.Chosen.Count > 1
        ? TaxiWaysRanking.CoverNote(People, CoverDrivers.Select(c => c.Driver.Seats))
        : TaxiWaysRanking.SeatsNote(People, Top.Driver.Seats);
    public bool Partial => Top.Link.Partial;
    public bool FareEstimated => CoverDrivers.Any(c => c.Link.FareEstimated);
}

public sealed class TaxiWaysPlan
{
    public string PlanKey { get; init; } = string.Empty;
    public List<TaxiWaysLegResult> Legs { get; init; } = new();

    public int Cars => Legs.Count;
    public int Total => Legs.Sum(l => l.ListedFare);
}

public sealed class TaxiWaysSearchResult
{
    public TaxiWaysAnchor From { get; init; } = null!;
    public TaxiWaysAnchor To { get; init; } = null!;
    public List<TaxiWaysCandidate> AllTheWay { get; init; } = new();
    public List<TaxiWaysPlan> Plans { get; init; } = new();
    public bool Truncated { get; set; }
    public int Expansions { get; set; }
}

public static class TaxiWaysKeys
{
    public const string Ver = "taxi:ways:ver";
    public const int NameSlugMax = 24;
    private const string PlanPrefix = "p1";
    private const string LegPrefix = "l1";
    private static readonly Regex KeyShape = new(@"^[A-Za-z0-9:_.~-]{3,200}$", RegexOptions.CultureInvariant);

    public static bool IsValidKey(string? key) => key != null && KeyShape.IsMatch(key);

    public static string PlaceKeyAtlas(long cityKey) => "a" + cityKey.ToString(CultureInfo.InvariantCulture);

    public static string PlaceKeyCluster(string name, double lat, double lng)
    {
        var slug = SearchKeys.Fold(name).Replace(' ', '_');
        if (slug.Length > NameSlugMax) slug = slug[..NameSlugMax].TrimEnd('_');
        if (slug.Length == 0) slug = "place";
        return $"c{slug}:{F2(lat)}:{F2(lng)}";
    }

    public static string VirtualKey(double lat, double lng, bool myLocation) =>
        $"{(myLocation ? "m" : "v")}:{F3(lat)}:{F3(lng)}";

    public static bool IsVirtual(string key) => key.StartsWith("v:", StringComparison.Ordinal) || key.StartsWith("m:", StringComparison.Ordinal);

    public static bool TryParseVirtual(string key, out double lat, out double lng, out bool myLocation)
    {
        lat = lng = 0;
        myLocation = key.StartsWith("m:", StringComparison.Ordinal);
        if (!IsVirtual(key)) return false;
        var parts = key.Split(':');
        return parts.Length == 3
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out lng)
            && lat is >= -90 and <= 90 && lng is >= -180 and <= 180;
    }

    public static string PlanKey(IReadOnlyList<string> placeKeys) => PlanPrefix + "~" + string.Join('~', placeKeys);

    public static string LegKey(string from, string to) => $"{LegPrefix}~{from}~{to}";

    public static bool TryParsePlanKey(string? planKey, out List<string> placeKeys, int maxCars = 3)
    {
        placeKeys = new List<string>();
        if (!IsValidKey(planKey)) return false;
        var parts = planKey!.Split('~');
        if (parts[0] != PlanPrefix || parts.Length < 3 || parts.Length > maxCars + 2) return false;
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || placeKeys.Contains(parts[i])) return false;
            placeKeys.Add(parts[i]);
        }
        return true;
    }

    public static bool TryParseLegKey(string? legKey, out string from, out string to)
    {
        from = to = string.Empty;
        if (!IsValidKey(legKey)) return false;
        var parts = legKey!.Split('~');
        if (parts.Length != 3 || parts[0] != LegPrefix || parts[1].Length == 0 || parts[2].Length == 0 || parts[1] == parts[2])
            return false;
        from = parts[1];
        to = parts[2];
        return true;
    }

    public static string PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

    public static string Plan(string ver, string from, string to, int people) => $"taxi:ways:{ver}:{from}:{to}:{people}";
    public static string Dest(string ver, string to) => $"taxi:ways:dest:{ver}:{to}";
    public static string District(double lat, double lng) => $"taxi:ways:district:{F2(lat)}:{F2(lng)}";

    private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string F3(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
}

public static class TaxiWaysGeo
{
    private const double MetresPerDegLat = 110_540;
    private const double MetresPerDegLng = 111_320;

    public static double DistanceMeters(TaxiWaysPoint a, TaxiWaysPoint b) => TaxiGeo.DistanceMeters(a.Lat, a.Lng, b.Lat, b.Lng);

    // Local equirectangular projection around p; fine for the few-km distances used here.
    public static (double Meters, double T) PointToSegment(TaxiWaysPoint p, TaxiWaysPoint a, TaxiWaysPoint b)
    {
        var k = Math.Cos(p.Lat * Math.PI / 180) * MetresPerDegLng;
        var ax = (a.Lng - p.Lng) * k; var ay = (a.Lat - p.Lat) * MetresPerDegLat;
        var bx = (b.Lng - p.Lng) * k; var by = (b.Lat - p.Lat) * MetresPerDegLat;
        var dx = bx - ax; var dy = by - ay;
        var len2 = dx * dx + dy * dy;
        var t = len2 <= 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / len2, 0, 1);
        var cx = ax + t * dx; var cy = ay + t * dy;
        return (Math.Sqrt(cx * cx + cy * cy), t);
    }

    public static double[] Cumulative(IReadOnlyList<TaxiWaysPoint> line)
    {
        var cum = new double[line.Count];
        for (var i = 1; i < line.Count; i++) cum[i] = cum[i - 1] + DistanceMeters(line[i - 1], line[i]);
        return cum;
    }

    // (distance to the line, fraction of the line's length where the nearest point lies)
    public static (double Meters, double Fraction) PointToPolyline(TaxiWaysPoint p, IReadOnlyList<TaxiWaysPoint> line, double[] cumulative)
    {
        if (line.Count == 0) return (double.MaxValue, 0);
        if (line.Count == 1) return (DistanceMeters(p, line[0]), 0);
        var best = double.MaxValue; var along = 0.0;
        for (var i = 0; i < line.Count - 1; i++)
        {
            var (m, t) = PointToSegment(p, line[i], line[i + 1]);
            if (m < best)
            {
                best = m;
                along = cumulative[i] + t * (cumulative[i + 1] - cumulative[i]);
            }
        }
        var total = cumulative[^1];
        return (best, total <= 0 ? 0 : along / total);
    }

    public static List<TaxiWaysPoint> Simplify(IReadOnlyList<TaxiWaysPoint> line, double toleranceMeters, int maxVertices)
    {
        if (line.Count <= 2) return line.ToList();
        var tolerance = Math.Max(1, toleranceMeters);
        while (true)
        {
            var keep = new bool[line.Count];
            keep[0] = keep[^1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, line.Count - 1));
            while (stack.Count > 0)
            {
                var (s, e) = stack.Pop();
                var maxD = 0.0; var idx = -1;
                for (var i = s + 1; i < e; i++)
                {
                    var (d, _) = PointToSegment(line[i], line[s], line[e]);
                    if (d > maxD) { maxD = d; idx = i; }
                }
                if (idx < 0 || maxD <= tolerance) continue;
                keep[idx] = true;
                stack.Push((s, idx));
                stack.Push((idx, e));
            }
            var result = new List<TaxiWaysPoint>();
            for (var i = 0; i < line.Count; i++) if (keep[i]) result.Add(line[i]);
            if (result.Count <= maxVertices || maxVertices < 2) return result;
            tolerance *= 2;
        }
    }

    public static List<TaxiWaysPoint> Slice(IReadOnlyList<TaxiWaysPoint> line, double[] cumulative, double from, double to)
    {
        var result = new List<TaxiWaysPoint>();
        if (line.Count < 2) return result;
        var total = cumulative[^1];
        var a = Math.Clamp(Math.Min(from, to), 0, 1) * total;
        var b = Math.Clamp(Math.Max(from, to), 0, 1) * total;
        result.Add(At(line, cumulative, a));
        for (var i = 0; i < line.Count; i++)
            if (cumulative[i] > a && cumulative[i] < b) result.Add(line[i]);
        result.Add(At(line, cumulative, b));
        return result;
    }

    private static TaxiWaysPoint At(IReadOnlyList<TaxiWaysPoint> line, double[] cumulative, double metres)
    {
        for (var i = 0; i < line.Count - 1; i++)
        {
            if (metres > cumulative[i + 1]) continue;
            var seg = cumulative[i + 1] - cumulative[i];
            var t = seg <= 0 ? 0 : (metres - cumulative[i]) / seg;
            return new TaxiWaysPoint(line[i].Lat + t * (line[i + 1].Lat - line[i].Lat), line[i].Lng + t * (line[i + 1].Lng - line[i].Lng));
        }
        return line[^1];
    }

    // GeoJSON LineString ({type, coordinates: [[lon, lat], ...]}) or a bare coordinates array; null when unreadable.
    public static List<TaxiWaysPoint>? ParseLineString(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var coords = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("coordinates", out var c) ? c
                : default;
            if (coords.ValueKind != JsonValueKind.Array) return null;
            var points = new List<TaxiWaysPoint>(coords.GetArrayLength());
            foreach (var p in coords.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 2) return null;
                var lng = p[0].GetDouble();
                var lat = p[1].GetDouble();
                if (lat is < -90 or > 90 || lng is < -180 or > 180) return null;
                points.Add(new TaxiWaysPoint(lat, lng));
            }
            return points.Count >= 2 ? points : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // 0.1 degree cells.
    public static long Cell(int latCell, int lngCell) => ((long)latCell << 32) | (uint)lngCell;
    public static int CellOf(double degrees) => (int)Math.Floor(degrees * 10);

    // Cells needed to cover a radius around a latitude (lng cells shrink towards the poles).
    public static (int Lat, int Lng) CellReach(double lat, double meters)
    {
        var latReach = (int)Math.Ceiling(meters / (MetresPerDegLat * 0.1));
        var cos = Math.Max(0.2, Math.Cos(lat * Math.PI / 180));
        var lngReach = (int)Math.Ceiling(meters / (MetresPerDegLng * cos * 0.1));
        return (latReach, lngReach);
    }

    public static IEnumerable<long> CellsAround(double lat, double lng, double meters)
    {
        var (rLat, rLng) = CellReach(lat, meters);
        var cLat = CellOf(lat); var cLng = CellOf(lng);
        for (var i = cLat - rLat; i <= cLat + rLat; i++)
            for (var j = cLng - rLng; j <= cLng + rLng; j++)
                yield return Cell(i, j);
    }
}

public static class TaxiWaysRanking
{
    public static bool SeatsFit(TaxiWaysDriver d, int people) => d.Seats.HasValue && d.Seats.Value >= people;

    // One car per started group of `seats`; unknown seats or people = one car.
    public static int CarsNeeded(int people, short? seats)
        => seats is > 0 && people > 0 ? Math.Max(1, (int)Math.Ceiling(people / (double)seats.Value)) : 1;

    public static string? SeatsNote(int people, short? seats)
    {
        var cars = CarsNeeded(people, seats);
        return cars > 1 ? $"Seats {seats} · {cars} cars for {people}" : null;
    }

    // Several distinct cars for one group: "Seats 7 + 4 · 2 cars for 10".
    public static string CoverNote(int people, IEnumerable<short?> seats)
    {
        var list = seats.ToList();
        return $"Seats {string.Join(" + ", list.Select(s => s?.ToString(CultureInfo.InvariantCulture) ?? "?"))} · {list.Count} cars for {people}";
    }

    // Best fit: seats for the group, (based here), whole route before a stretch, replies in app, plan rank, fare, id.
    public static IComparer<TaxiWaysCandidate> Compare(int people, bool basedHereFirst = false) =>
        Comparer<TaxiWaysCandidate>.Create((x, y) => BestFit(x, y, people, basedHereFirst));

    public static IComparer<TaxiWaysCandidate> LowestPrice(int people) =>
        Comparer<TaxiWaysCandidate>.Create((x, y) =>
        {
            var c = x.Link.EstimatedFare.CompareTo(y.Link.EstimatedFare);
            return c != 0 ? c : BestFit(x, y, people, false);
        });

    public static int BestFit(TaxiWaysCandidate x, TaxiWaysCandidate y, int people, bool basedHereFirst)
    {
        var c = SeatsFit(y.Driver, people).CompareTo(SeatsFit(x.Driver, people));
        if (c != 0) return c;
        if (basedHereFirst)
        {
            c = y.BasedHere.CompareTo(x.BasedHere);
            if (c != 0) return c;
        }
        c = x.Link.Partial.CompareTo(y.Link.Partial);
        if (c != 0) return c;
        c = (y.Driver.TripReplyLabel == TaxiTripReplyLabels.App).CompareTo(x.Driver.TripReplyLabel == TaxiTripReplyLabels.App);
        if (c != 0) return c;
        c = y.Driver.PriorityRank.CompareTo(x.Driver.PriorityRank);
        if (c != 0) return c;
        c = x.Link.EstimatedFare.CompareTo(y.Link.EstimatedFare);
        if (c != 0) return c;
        c = x.Driver.TaxiId.CompareTo(y.Driver.TaxiId);
        return c != 0 ? c : x.Link.RouteId.CompareTo(y.Link.RouteId);
    }
}

public static class TaxiWaysClustering
{
    private readonly record struct EndPoint(int Row, bool IsFrom, string Name, double Lat, double Lng);

    // Route ends with no Atlas city near them, grouped without depending on input order or names: the densest end
    // (most other ends within SamePlaceMeters; ties by lat, lng, route id, side) becomes a centre, every free end within
    // SamePlaceMeters of it joins, repeat. A centre is a real end point and never moves, so every member is within
    // SamePlaceMeters of the stored place (the radius journey refresh matches with).
    private static List<(EndPoint Centre, List<EndPoint> Members)> GroupEnds(
        IReadOnlyList<EndPoint> ends, Func<EndPoint, Guid> routeOf, double meters)
    {
        var grid = new Dictionary<long, List<int>>();
        for (var i = 0; i < ends.Count; i++)
        {
            var cell = TaxiWaysGeo.Cell(TaxiWaysGeo.CellOf(ends[i].Lat), TaxiWaysGeo.CellOf(ends[i].Lng));
            if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = new();
            list.Add(i);
        }

        List<int> Near(int i)
        {
            var e = ends[i];
            var near = new List<int>();
            foreach (var cell in TaxiWaysGeo.CellsAround(e.Lat, e.Lng, meters))
                if (grid.TryGetValue(cell, out var list))
                    foreach (var j in list)
                        if (TaxiGeo.DistanceMeters(e.Lat, e.Lng, ends[j].Lat, ends[j].Lng) <= meters) near.Add(j);
            return near;
        }

        var neighbours = Enumerable.Range(0, ends.Count).Select(Near).ToList();
        var order = Enumerable.Range(0, ends.Count).ToList();
        order.Sort((a, b) =>
        {
            var c = neighbours[b].Count.CompareTo(neighbours[a].Count);
            if (c != 0) return c;
            c = ends[a].Lat.CompareTo(ends[b].Lat);
            if (c != 0) return c;
            c = ends[a].Lng.CompareTo(ends[b].Lng);
            if (c != 0) return c;
            c = routeOf(ends[a]).CompareTo(routeOf(ends[b]));
            return c != 0 ? c : ends[b].IsFrom.CompareTo(ends[a].IsFrom);
        });
        var rank = new int[ends.Count];
        for (var i = 0; i < order.Count; i++) rank[order[i]] = i;

        var taken = new bool[ends.Count];
        var groups = new List<(EndPoint, List<EndPoint>)>();
        foreach (var i in order)
        {
            if (taken[i]) continue;
            var members = neighbours[i].Where(j => !taken[j]).OrderBy(j => rank[j]).ToList();
            foreach (var j in members) taken[j] = true;
            groups.Add((ends[i], members.Select(j => ends[j]).ToList()));
        }
        return groups;
    }

    // The most common name among the members; a tie goes to the member nearest the centre in group order.
    private static string NameOf(EndPoint centre, IReadOnlyList<EndPoint> members)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var first = new List<string>();
        foreach (var m in members)
        {
            var name = (m.Name ?? string.Empty).Trim();
            if (name.Length == 0) continue;
            if (!counts.TryGetValue(name, out var n)) first.Add(name);
            counts[name] = n + 1;
        }
        if (first.Count == 0) return (centre.Name ?? string.Empty).Trim();
        return first.OrderByDescending(n => counts[n]).ThenBy(n => first.IndexOf(n)).First();
    }

    // Places within `meters` of a point, nearest first (ties by key).
    private static List<string> PlacesNear(
        Dictionary<string, TaxiWaysPlace> places, Dictionary<long, List<string>> placeGrid, double lat, double lng, double meters)
    {
        var hits = new List<(string Key, double D)>();
        foreach (var cell in TaxiWaysGeo.CellsAround(lat, lng, meters))
        {
            if (!placeGrid.TryGetValue(cell, out var keys)) continue;
            foreach (var k in keys)
            {
                var p = places[k];
                var d = TaxiGeo.DistanceMeters(lat, lng, p.Lat, p.Lng);
                if (d <= meters) hits.Add((k, d));
            }
        }
        return hits.OrderBy(h => h.D).ThenBy(h => h.Key, StringComparer.Ordinal).Select(h => h.Key).ToList();
    }

    private static List<string> EndPlaces(string primary, List<string> near)
    {
        var list = new List<string> { primary };
        list.AddRange(near.Where(k => k != primary));
        return list;
    }

    public static TaxiWaysNetworkSnapshot Build(
        IReadOnlyList<TaxiWaysSourceRow> rows, IReadOnlyList<TripCityInfo>? atlasCities,
        TaxiWaysOptions o, string version, DateTime builtAt)
    {
        var places = new Dictionary<string, TaxiWaysPlace>(StringComparer.Ordinal);
        var fromPlaceOf = new string[rows.Count];
        var toPlaceOf = new string[rows.Count];

        var atlasGrid = new Dictionary<long, List<TripCityInfo>>();
        foreach (var city in atlasCities ?? [])
        {
            if (city.Latitude is < -90 or > 90 || city.Longitude is < -180 or > 180) continue;
            var cell = TaxiWaysGeo.Cell(TaxiWaysGeo.CellOf(city.Latitude), TaxiWaysGeo.CellOf(city.Longitude));
            if (!atlasGrid.TryGetValue(cell, out var list)) atlasGrid[cell] = list = new();
            list.Add(city);
        }

        var unmatched = new List<EndPoint>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            foreach (var end in new[] { new EndPoint(i, true, r.FromName, r.FromLat, r.FromLng), new EndPoint(i, false, r.ToName, r.ToLat, r.ToLng) })
            {
                var city = NearestAtlas(atlasGrid, end.Lat, end.Lng, o.SamePlaceMeters);
                if (city == null) { unmatched.Add(end); continue; }
                var key = TaxiWaysKeys.PlaceKeyAtlas(city.CityKey);
                if (!places.ContainsKey(key))
                    places[key] = new TaxiWaysPlace { Key = key, Name = city.Name, Lat = city.Latitude, Lng = city.Longitude, CityKey = city.CityKey };
                if (end.IsFrom) fromPlaceOf[i] = key; else toPlaceOf[i] = key;
            }
        }

        var usedKeys = new HashSet<string>(places.Keys, StringComparer.Ordinal);
        foreach (var (centre, members) in GroupEnds(unmatched, e => rows[e.Row].RouteId, o.SamePlaceMeters))
        {
            var name = NameOf(centre, members);
            var key = TaxiWaysKeys.PlaceKeyCluster(name, centre.Lat, centre.Lng);
            var unique = key; var n = 2;
            while (!usedKeys.Add(unique)) unique = $"{key}-{n++}";
            places[unique] = new TaxiWaysPlace { Key = unique, Name = name.Length == 0 ? TaxiWaysLabels.Stop : name, Lat = centre.Lat, Lng = centre.Lng };
            foreach (var m in members)
                if (m.IsFrom) fromPlaceOf[m.Row] = unique; else toPlaceOf[m.Row] = unique;
        }

        var placeGrid = new Dictionary<long, List<string>>();
        foreach (var place in places.Values)
        {
            var cell = TaxiWaysGeo.Cell(TaxiWaysGeo.CellOf(place.Lat), TaxiWaysGeo.CellOf(place.Lng));
            if (!placeGrid.TryGetValue(cell, out var list)) placeGrid[cell] = list = new();
            list.Add(place.Key);
        }

        var drivers = new Dictionary<Guid, TaxiWaysDriver>();
        var routes = new Dictionary<Guid, TaxiWaysRouteLine>();
        var links = new List<TaxiWaysLink>();
        var routeGrid = new Dictionary<long, List<Guid>>();

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (!drivers.ContainsKey(r.TaxiId))
                drivers[r.TaxiId] = new TaxiWaysDriver
                {
                    TaxiId = r.TaxiId, UserId = r.UserId, DriverName = r.DriverName, VehicleTypeName = r.VehicleTypeName,
                    VehicleCategory = r.VehicleCategory, Seats = r.Seats, PlateDisplay = r.PlateDisplay, CityId = r.CityId,
                    CityName = r.CityName, BaseLat = r.BaseLat, BaseLng = r.BaseLng, LocalDayRate = r.LocalDayRate,
                    PriorityRank = r.PriorityRank, IsFeatured = r.IsFeatured, TripReplyLabel = r.TripReplyLabel,
                };

            var fromKey = fromPlaceOf[i];
            var toKey = toPlaceOf[i];
            if (fromKey == toKey || routes.ContainsKey(r.RouteId)) continue;

            // A route end serves every place within SamePlaceMeters of it (the radius /taxis/search matches with), not only
            // the nearest one, so two pins of one town on either side of a place boundary still reach the same drivers.
            var fromPlaces = EndPlaces(fromKey, PlacesNear(places, placeGrid, r.FromLat, r.FromLng, o.SamePlaceMeters));
            var toPlaces = EndPlaces(toKey, PlacesNear(places, placeGrid, r.ToLat, r.ToLng, o.SamePlaceMeters));
            var endKeys = new HashSet<string>(fromPlaces.Concat(toPlaces), StringComparer.Ordinal);

            var line = LineOf(r, o);
            var cumulative = line.Count >= 2 ? TaxiWaysGeo.Cumulative(line) : [];
            var passes = line.Count >= 2 ? PassesOf(line, cumulative, endKeys, places, placeGrid, o) : [];

            var route = new TaxiWaysRouteLine
            {
                RouteId = r.RouteId, TaxiId = r.TaxiId, FromName = r.FromName, ToName = r.ToName,
                FromPlace = fromKey, ToPlace = toKey, FromPlaces = fromPlaces, ToPlaces = toPlaces, Fare = r.Fare, TripType = r.TripType,
                DistanceKm = r.DistanceKm, Minutes = r.Minutes, Line = line, Cumulative = cumulative, Passes = passes,
            };
            routes[r.RouteId] = route;

            var chain = new List<(IReadOnlyList<string> Keys, double Fraction)> { (fromPlaces, 0) };
            chain.AddRange(passes.Select(p => ((IReadOnlyList<string>)[p.PlaceKey], p.Fraction)));
            chain.Add((toPlaces, 1));
            var seenPairs = new HashSet<string>(StringComparer.Ordinal);
            for (var a = 0; a < chain.Count - 1; a++)
                for (var b = a + 1; b < chain.Count; b++)
                {
                    var via = chain.Skip(a + 1).Take(b - a - 1).Select(x => x.Keys[0]).ToList();
                    foreach (var fk in chain[a].Keys)
                        foreach (var tk in chain[b].Keys)
                        {
                            if (fk == tk || !seenPairs.Add(TaxiWaysKeys.PairKey(fk, tk))) continue;
                            links.Add(new TaxiWaysLink
                            {
                                RouteId = r.RouteId, TaxiId = r.TaxiId, FromPlace = fk, ToPlace = tk,
                                Fare = r.Fare, TripType = r.TripType, Partial = !(a == 0 && b == chain.Count - 1),
                                FromFraction = chain[a].Fraction, ToFraction = chain[b].Fraction,
                                Via = via,
                            });
                        }
                }

            if (line.Count >= 2) IndexRoute(routeGrid, r.RouteId, line, o.AlongRoadMeters);
        }

        var adjacency = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var pairs = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < links.Count; i++)
        {
            var l = links[i];
            Add(adjacency, l.FromPlace, i);
            Add(adjacency, l.ToPlace, i);
            Add(pairs, TaxiWaysKeys.PairKey(l.FromPlace, l.ToPlace), i);
        }

        return new TaxiWaysNetworkSnapshot
        {
            Version = version, BuiltAt = builtAt, Places = places, Links = links, Adjacency = adjacency, Pairs = pairs,
            Drivers = drivers, Routes = routes, PlaceGrid = placeGrid, RouteGrid = routeGrid,
        };
    }

    private static void Add<TKey>(Dictionary<TKey, List<int>> map, TKey key, int value) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new();
        list.Add(value);
    }

    private static TripCityInfo? NearestAtlas(Dictionary<long, List<TripCityInfo>> grid, double lat, double lng, double meters)
    {
        TripCityInfo? best = null; var bestD = double.MaxValue;
        foreach (var cell in TaxiWaysGeo.CellsAround(lat, lng, meters))
        {
            if (!grid.TryGetValue(cell, out var list)) continue;
            foreach (var c in list)
            {
                var d = TaxiGeo.DistanceMeters(lat, lng, c.Latitude, c.Longitude);
                if (d <= meters && (d < bestD || (d == bestD && best != null && c.CityKey < best.CityKey))) { best = c; bestD = d; }
            }
        }
        return best;
    }

    // Geometry kept only when its ends sit at the route's ends (a stale row from before an ends change is dropped).
    private static List<TaxiWaysPoint> LineOf(TaxiWaysSourceRow r, TaxiWaysOptions o)
    {
        var parsed = TaxiWaysGeo.ParseLineString(r.GeometryJson);
        if (parsed == null) return [];
        var from = new TaxiWaysPoint(r.FromLat, r.FromLng);
        var to = new TaxiWaysPoint(r.ToLat, r.ToLng);
        var tol = o.GeometryEndToleranceMeters;
        if (TaxiWaysGeo.DistanceMeters(parsed[0], to) <= tol && TaxiWaysGeo.DistanceMeters(parsed[^1], from) <= tol
            && TaxiWaysGeo.DistanceMeters(parsed[0], from) > tol)
            parsed.Reverse();
        if (TaxiWaysGeo.DistanceMeters(parsed[0], from) > tol || TaxiWaysGeo.DistanceMeters(parsed[^1], to) > tol) return [];
        return parsed.Count > o.MaxVertices ? TaxiWaysGeo.Simplify(parsed, o.SimplifyMeters, o.MaxVertices) : parsed;
    }

    private static List<TaxiWaysPass> PassesOf(
        IReadOnlyList<TaxiWaysPoint> line, double[] cumulative, IReadOnlySet<string> endKeys,
        Dictionary<string, TaxiWaysPlace> places, Dictionary<long, List<string>> placeGrid, TaxiWaysOptions o)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < line.Count - 1; i++)
            foreach (var cell in SegmentCells(line[i], line[i + 1], o.AlongRoadMeters))
                if (placeGrid.TryGetValue(cell, out var keys))
                    foreach (var k in keys)
                        if (!endKeys.Contains(k)) candidates.Add(k);

        var hits = new List<TaxiWaysPass>();
        foreach (var key in candidates)
        {
            var place = places[key];
            var (meters, fraction) = TaxiWaysGeo.PointToPolyline(new TaxiWaysPoint(place.Lat, place.Lng), line, cumulative);
            if (meters <= o.AlongRoadMeters && fraction > 0 && fraction < 1) hits.Add(new TaxiWaysPass(key, fraction, meters));
        }
        return hits
            .OrderBy(h => h.DistanceMeters).ThenBy(h => h.PlaceKey, StringComparer.Ordinal)
            .Take(o.MaxPassPlaces)
            .OrderBy(h => h.Fraction).ThenBy(h => h.PlaceKey, StringComparer.Ordinal)
            .ToList();
    }

    internal static IEnumerable<long> SegmentCells(TaxiWaysPoint a, TaxiWaysPoint b, double padMeters)
    {
        var (rLat, rLng) = TaxiWaysGeo.CellReach(Math.Max(Math.Abs(a.Lat), Math.Abs(b.Lat)), padMeters);
        var minLat = TaxiWaysGeo.CellOf(Math.Min(a.Lat, b.Lat)) - rLat;
        var maxLat = TaxiWaysGeo.CellOf(Math.Max(a.Lat, b.Lat)) + rLat;
        var minLng = TaxiWaysGeo.CellOf(Math.Min(a.Lng, b.Lng)) - rLng;
        var maxLng = TaxiWaysGeo.CellOf(Math.Max(a.Lng, b.Lng)) + rLng;
        for (var i = minLat; i <= maxLat; i++)
            for (var j = minLng; j <= maxLng; j++)
                yield return TaxiWaysGeo.Cell(i, j);
    }

    private static void IndexRoute(Dictionary<long, List<Guid>> grid, Guid routeId, IReadOnlyList<TaxiWaysPoint> line, double padMeters)
    {
        var cells = new HashSet<long>();
        for (var i = 0; i < line.Count - 1; i++)
            foreach (var cell in SegmentCells(line[i], line[i + 1], padMeters)) cells.Add(cell);
        foreach (var cell in cells)
        {
            if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = new();
            list.Add(routeId);
        }
    }
}

public static class TaxiWaysLabels
{
    public const string MyLocation = "My location", Pickup = "Pickup point", Drop = "Drop point", Stop = "Stop";

    // A place name is a label, never a blocker: a blank name falls back to a fixed label by the key and the end it sits at.
    public static string Fallback(string? key, bool start, bool end)
    {
        if (key != null && key.StartsWith("m:", StringComparison.Ordinal)) return MyLocation;
        return start ? Pickup : end ? Drop : Stop;
    }

    public static string Of(string? typed, string? key, bool start, bool end)
    {
        var name = TripTextSafety.Clean(typed, JourneyRules.NameMax);
        return name.Length > 0 ? name : Fallback(key, start, end);
    }
}

public static class TaxiWaysSearchEngine
{
    // Nearest place within SamePlaceMeters; otherwise a virtual point at the 3-decimal coordinates.
    public static TaxiWaysAnchor Resolve(TaxiWaysNetworkSnapshot s, double lat, double lng, bool myLocation, TaxiWaysOptions o, string? fallbackName = null)
    {
        var place = NearestPlace(s, lat, lng, o.SamePlaceMeters, _ => true);
        if (place != null) return AnchorOf(place);
        var key = TaxiWaysKeys.VirtualKey(lat, lng, myLocation);
        TaxiWaysKeys.TryParseVirtual(key, out var vLat, out var vLng, out _);
        return Virtual(s, key, vLat, vLng, myLocation, o, fallbackName);
    }

    // Rebuilds an anchor from a key of a plan or leg key; null when the key is a place no longer in the snapshot.
    public static TaxiWaysAnchor? AnchorFromKey(TaxiWaysNetworkSnapshot s, string key, TaxiWaysOptions o, bool isEnd = false)
    {
        if (s.Places.TryGetValue(key, out var place)) return AnchorOf(place);
        if (!TaxiWaysKeys.TryParseVirtual(key, out var lat, out var lng, out var my)) return null;
        return Virtual(s, key, lat, lng, my, o, TaxiWaysLabels.Fallback(key, !isEnd, isEnd));
    }

    public static TaxiWaysAnchor AnchorOf(TaxiWaysPlace place) =>
        new() { Key = place.Key, Name = place.Name, Lat = place.Lat, Lng = place.Lng, Place = place };

    public static TaxiWaysPlace? NearestPlace(TaxiWaysNetworkSnapshot s, double lat, double lng, double meters, Func<TaxiWaysPlace, bool> filter)
    {
        TaxiWaysPlace? best = null; var bestD = double.MaxValue;
        foreach (var cell in TaxiWaysGeo.CellsAround(lat, lng, meters))
        {
            if (!s.PlaceGrid.TryGetValue(cell, out var keys)) continue;
            foreach (var k in keys)
            {
                var p = s.Places[k];
                var d = TaxiGeo.DistanceMeters(lat, lng, p.Lat, p.Lng);
                if (d <= meters && (d < bestD || (d == bestD && best != null && string.CompareOrdinal(p.Key, best.Key) < 0)) && filter(p))
                {
                    best = p; bestD = d;
                }
            }
        }
        return best;
    }

    private static TaxiWaysAnchor Virtual(TaxiWaysNetworkSnapshot s, string key, double lat, double lng, bool myLocation, TaxiWaysOptions o, string? name)
    {
        var anchor = new TaxiWaysAnchor { Key = key, Name = string.IsNullOrWhiteSpace(name) ? TaxiWaysLabels.Fallback(key, true, false) : name, Lat = lat, Lng = lng, MyLocation = myLocation };
        var point = new TaxiWaysPoint(lat, lng);
        var cell = TaxiWaysGeo.Cell(TaxiWaysGeo.CellOf(lat), TaxiWaysGeo.CellOf(lng));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (s.RouteGrid.TryGetValue(cell, out var routeIds))
        {
            foreach (var routeId in routeIds.Distinct())
            {
                var route = s.Routes[routeId];
                var (meters, fv) = TaxiWaysGeo.PointToPolyline(point, route.Line, route.Cumulative);
                if (meters > o.AlongRoadMeters) continue;
                anchor.RouteFractions[routeId] = fv;
                var chain = Chain(route);
                foreach (var (placeKey, fp) in chain)
                {
                    if (Math.Abs(fp - fv) < 1e-9) continue;
                    var forward = fp > fv;
                    var between = chain.Where(c => forward ? c.Fraction > fv && c.Fraction < fp : c.Fraction < fv && c.Fraction > fp)
                        .OrderBy(c => Math.Abs(c.Fraction - fv)).Select(c => c.Key).ToList();
                    if (!seen.Add($"{routeId}|{placeKey}")) continue;
                    anchor.Links.Add(new TaxiWaysLink
                    {
                        RouteId = routeId, TaxiId = route.TaxiId,
                        FromPlace = forward ? key : placeKey, ToPlace = forward ? placeKey : key,
                        Fare = route.Fare, TripType = route.TripType, Partial = true,
                        FromFraction = Math.Min(fv, fp), ToFraction = Math.Max(fv, fp),
                        Via = forward ? between : Enumerable.Reverse(between).ToList(),
                        MeetAtPlace = between.Count > 0 ? between[0] : null,
                    });
                }
            }
        }

        if (myLocation)
        {
            var near = new List<(TaxiWaysPlace Place, double D)>();
            foreach (var c in TaxiWaysGeo.CellsAround(lat, lng, o.MyLocationNearEndMeters))
            {
                if (!s.PlaceGrid.TryGetValue(c, out var keys)) continue;
                foreach (var k in keys)
                {
                    var p = s.Places[k];
                    var d = TaxiGeo.DistanceMeters(lat, lng, p.Lat, p.Lng);
                    if (d <= o.MyLocationNearEndMeters) near.Add((p, d));
                }
            }
            foreach (var (endPlace, _) in near.OrderBy(n => n.D).ThenBy(n => n.Place.Key, StringComparer.Ordinal))
            {
                foreach (var l in s.LinksAt(endPlace.Key))
                {
                    var other = l.OtherEnd(endPlace.Key);
                    if (!seen.Add($"{l.RouteId}|{other}")) continue;
                    var forward = l.FromPlace == endPlace.Key;
                    anchor.Links.Add(new TaxiWaysLink
                    {
                        RouteId = l.RouteId, TaxiId = l.TaxiId,
                        FromPlace = forward ? key : other, ToPlace = forward ? other : key,
                        Fare = l.Fare, TripType = l.TripType, Partial = l.Partial,
                        FromFraction = l.FromFraction, ToFraction = l.ToFraction, Via = l.Via,
                        MeetAtPlace = endPlace.Key,
                    });
                }
            }
        }
        return anchor;
    }

    private static List<(string Key, double Fraction)> Chain(TaxiWaysRouteLine route)
    {
        var chain = new List<(string, double)> { (route.FromPlace, 0) };
        chain.AddRange(route.Passes.Select(p => (p.PlaceKey, p.Fraction)));
        chain.Add((route.ToPlace, 1));
        return chain;
    }

    public static IEnumerable<TaxiWaysLink> LinksBetween(TaxiWaysNetworkSnapshot s, TaxiWaysAnchor a, TaxiWaysAnchor b)
    {
        if (a.Key == b.Key) return [];
        if (a.Resolved && b.Resolved) return s.LinksBetweenPlaces(a.Key, b.Key);
        if (!a.Resolved && b.Resolved) return a.Links.Where(l => l.OtherEnd(a.Key) == b.Key);
        if (a.Resolved && !b.Resolved) return b.Links.Where(l => l.OtherEnd(b.Key) == a.Key);
        return VirtualPair(s, a, b);
    }

    private static IEnumerable<TaxiWaysLink> VirtualPair(TaxiWaysNetworkSnapshot s, TaxiWaysAnchor a, TaxiWaysAnchor b)
    {
        foreach (var (routeId, fa) in a.RouteFractions)
        {
            if (!b.RouteFractions.TryGetValue(routeId, out var fb) || Math.Abs(fa - fb) < 1e-9) continue;
            var route = s.Routes[routeId];
            var forward = fb > fa;
            var lo = Math.Min(fa, fb); var hi = Math.Max(fa, fb);
            var via = route.Passes.Where(p => p.Fraction > lo && p.Fraction < hi).Select(p => p.PlaceKey).ToList();
            yield return new TaxiWaysLink
            {
                RouteId = routeId, TaxiId = route.TaxiId,
                FromPlace = forward ? a.Key : b.Key, ToPlace = forward ? b.Key : a.Key,
                Fare = route.Fare, TripType = route.TripType, Partial = true,
                FromFraction = lo, ToFraction = hi, Via = via,
            };
        }
    }

    public static IEnumerable<string> Neighbours(TaxiWaysNetworkSnapshot s, TaxiWaysAnchor a) =>
        (a.Resolved ? s.LinksAt(a.Key) : a.Links).Select(l => l.OtherEnd(a.Key)).Distinct(StringComparer.Ordinal);

    // One row per taxi on the stretch a -> b, ranked. excludeRoute: the previous leg's route is never split into a second car.
    public static List<TaxiWaysCandidate> Candidates(
        TaxiWaysNetworkSnapshot s, TaxiWaysAnchor a, TaxiWaysAnchor b, int people, TaxiWaysOptions o,
        bool basedHereFirst = false, Guid? excludeRoute = null)
    {
        var comparer = TaxiWaysRanking.Compare(people, basedHereFirst);
        var best = new Dictionary<Guid, TaxiWaysCandidate>();
        foreach (var link in LinksBetween(s, a, b))
        {
            if (link.RouteId == excludeRoute || !s.Drivers.TryGetValue(link.TaxiId, out var driver)) continue;
            var basedHere = driver.BaseLat.HasValue && driver.BaseLng.HasValue
                && TaxiGeo.DistanceMeters(a.Lat, a.Lng, driver.BaseLat.Value, driver.BaseLng.Value) <= o.BasedHereMeters;
            var candidate = new TaxiWaysCandidate(driver, link, link.FromPlace != a.Key, basedHere);
            if (!best.TryGetValue(link.TaxiId, out var current) || comparer.Compare(candidate, current) < 0)
                best[link.TaxiId] = candidate;
        }
        var list = best.Values.ToList();
        list.Sort(comparer);
        return list;
    }

    public static TaxiWaysStop StopOf(TaxiWaysAnchor a) => new(a.Key, a.Name, a.Lat, a.Lng);

    // Legs over an ordered list of anchors; null when any stretch has no driver left.
    public static TaxiWaysPlan? BuildPlan(TaxiWaysNetworkSnapshot s, IReadOnlyList<TaxiWaysAnchor> stops, int people, TaxiWaysOptions o)
    {
        var legs = new List<TaxiWaysLegResult>();
        Guid? previousRoute = null;
        for (var i = 0; i < stops.Count - 1; i++)
        {
            var a = stops[i]; var b = stops[i + 1];
            var leg = BuildLeg(s, a, b, i + 1, people, o, basedHereFirst: i == 0 && a.MyLocation, excludeRoute: previousRoute);
            if (leg == null) return null;
            if (i == 0 && !a.Resolved && leg.Top.Link.MeetAtPlace is { } meet && s.Places.TryGetValue(meet, out var meetPlace))
                leg.MeetingPlace = meetPlace.Name;
            if (i > 0)
            {
                var prev = legs[^1];
                if (IsPassPlace(s, prev.Top.Link, a.Key) || IsPassPlace(s, leg.Top.Link, a.Key))
                    leg.MeetingPlace = a.Name;
            }
            legs.Add(leg);
            previousRoute = leg.Top.Link.RouteId;
        }
        return new TaxiWaysPlan { PlanKey = TaxiWaysKeys.PlanKey(stops.Select(x => x.Key).ToList()), Legs = legs };
    }

    // One stretch a -> b: every taxi on it ranked, the group's seat cover and the top driver's road minutes; null = nobody.
    public static TaxiWaysLegResult? BuildLeg(
        TaxiWaysNetworkSnapshot s, TaxiWaysAnchor a, TaxiWaysAnchor b, int index, int people, TaxiWaysOptions o,
        bool basedHereFirst, Guid? excludeRoute)
    {
        var drivers = Candidates(s, a, b, people, o, basedHereFirst, excludeRoute);
        if (drivers.Count == 0) return null;
        return new TaxiWaysLegResult
        {
            Index = index, LegKey = TaxiWaysKeys.LegKey(a.Key, b.Key), From = StopOf(a), To = StopOf(b), Drivers = drivers,
            People = people, CoverPool = o.DetailDriversPerLeg, Minutes = MinutesOf(s, drivers[0].Link),
        };
    }

    public static int? MinutesOf(TaxiWaysNetworkSnapshot s, TaxiWaysLink link) =>
        s.Routes.TryGetValue(link.RouteId, out var route) && route.Minutes is { } min ? (int)Math.Ceiling(min * link.Span) : null;

    // True when the change happens at a place the driver passes, not at one of his route's own ends.
    private static bool IsPassPlace(TaxiWaysNetworkSnapshot s, TaxiWaysLink link, string placeKey) =>
        s.Routes.TryGetValue(link.RouteId, out var route)
        && route.FromPlace != placeKey && route.ToPlace != placeKey
        && !route.FromPlaces.Contains(placeKey) && !route.ToPlaces.Contains(placeKey);

    public static TaxiWaysSearchResult Search(TaxiWaysNetworkSnapshot s, TaxiWaysAnchor from, TaxiWaysAnchor to, int people, TaxiWaysOptions o)
    {
        var result = new TaxiWaysSearchResult { From = from, To = to };
        if (from.Key == to.Key) return result;

        result.AllTheWay.AddRange(Candidates(s, from, to, people, o, basedHereFirst: from.MyLocation));

        var (plans, relaxed) = TaxiWaysChainSearch.Plans(s, from, to, people, o);
        result.Plans.AddRange(plans);
        if (DirectCoverPlan(s, from, to, result.AllTheWay, people, o) is { } direct)
        {
            // One stretch, no change: a chain that costs as much or more and is no quicker gives way (TaxiWaysChainSearch.Front).
            var directMinutes = PlanMinutes(direct);
            result.Plans.RemoveAll(p => direct.Total <= p.Total
                && (directMinutes == null || PlanMinutes(p) is not { } pm || directMinutes <= pm));
            result.Plans.Insert(0, direct);
            var max = Math.Max(1, o.MaxPlans);
            if (result.Plans.Count > max) result.Plans.RemoveRange(max, result.Plans.Count - max);
        }
        result.Expansions = relaxed;
        return result;
    }

    // All the way with several drivers' cars (plan key p1~from~to, one leg): kept only when the least-cost seat cover uses two
    // or more distinct cars and is cheaper than every single driver bringing his own cars for the whole group.
    public static TaxiWaysPlan? DirectCoverPlan(
        TaxiWaysNetworkSnapshot s, TaxiWaysAnchor from, TaxiWaysAnchor to, IReadOnlyList<TaxiWaysCandidate> allTheWay, int people,
        TaxiWaysOptions o)
    {
        if (allTheWay.Count < 2 || people <= 1) return null;
        var plan = BuildPlan(s, [from, to], people, o);
        if (plan == null || plan.Legs.Count != 1) return null;
        var cover = plan.Legs[0].Cover;
        if (!cover.Covered || cover.Chosen.Count < 2) return null;
        var single = allTheWay.Min(c => (long)c.Link.EstimatedFare * TaxiWaysRanking.CarsNeeded(people, c.Driver.Seats));
        return cover.Fare < single ? plan : null;
    }

    private static int? PlanMinutes(TaxiWaysPlan plan)
    {
        var sum = 0;
        foreach (var leg in plan.Legs)
        {
            if (leg.Minutes is not { } m) return null;
            sum += m;
        }
        return sum;
    }

    // Places from which `to` is reachable in at most maxCars legs (reverse BFS), for "Reach X by train or bus".
    public static TaxiWaysPlace? NearestReachingPlace(TaxiWaysNetworkSnapshot s, TaxiWaysAnchor from, TaxiWaysAnchor to, TaxiWaysOptions o)
    {
        var reach = new HashSet<string>(StringComparer.Ordinal);
        var frontier = Neighbours(s, to).ToList();
        var budget = o.MaxExpansions;
        for (var depth = 0; depth < o.MaxCars && frontier.Count > 0 && budget > 0; depth++)
        {
            var next = new List<string>();
            foreach (var k in frontier)
            {
                if (--budget < 0) break;
                if (k == to.Key || !reach.Add(k)) continue;
                if (depth + 1 < o.MaxCars) next.AddRange(s.LinksAt(k).Select(l => l.OtherEnd(k)));
            }
            frontier = next;
        }
        reach.Remove(from.Key);
        TaxiWaysPlace? best = null; var bestD = double.MaxValue;
        foreach (var key in reach)
        {
            if (!s.Places.TryGetValue(key, out var p)) continue;
            var d = TaxiGeo.DistanceMeters(from.Lat, from.Lng, p.Lat, p.Lng);
            if (d <= o.ReachMaxKm * 1000 && (d < bestD || (d == bestD && best != null && string.CompareOrdinal(p.Key, best.Key) < 0)))
            {
                best = p; bestD = d;
            }
        }
        return best;
    }
}

using System.Globalization;

namespace RentNearBy.Core.Models;

public readonly record struct GeoCell(long LatIndex, long LngIndex, int Decimals)
{
    public double Step => Math.Pow(10, -Decimals);
    public double CenterLat => (LatIndex + 0.5) * Step;
    public double CenterLng => (LngIndex + 0.5) * Step;
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Decimals}:{LatIndex}:{LngIndex}");
}

public readonly record struct GeoPoint(double Lat, double Lng, short Rank);

public readonly record struct GeoHit(int Index, double DistanceKm);

// A point cache keys on a grid cell, caches the candidate set of the WHOLE cell (query radius widened by the
// cell's half diagonal) and refines it per caller from the exact point, so every caller gets the exact answer.
public static class GeoCacheKey
{
    public const int NearbyDecimals = 3;
    public const double RadiusStepKm = 0.1;
    private const double SlackKm = 0.001;
    private const double MetricTolerance = 1e-9;

    public static GeoCell Cell(double lat, double lng, int decimals = NearbyDecimals)
    {
        var scale = Math.Pow(10, decimals);
        return new GeoCell((long)Math.Floor(lat * scale), (long)Math.Floor(lng * scale), decimals);
    }

    // Upper bound (km) of the distance from the cell centre to any point of the cell.
    public static double HalfDiagonalKm(GeoCell cell)
    {
        var half = cell.Step / 2;
        var max = 0.0;
        foreach (var dLat in new[] { -half, half })
            foreach (var dLng in new[] { -half, half })
                max = Math.Max(max, GeoMath.HaversineKm(cell.CenterLat, cell.CenterLng, cell.CenterLat + dLat, cell.CenterLng + dLng));
        return max + SlackKm;
    }

    // Radius rounded UP to the key step, so a cached candidate set always covers the caller's exact radius.
    public static double RadiusKey(double radiusKm)
        => Math.Ceiling(Math.Round(radiusKm / RadiusStepKm, 9)) * RadiusStepKm;

    public static double CandidateRadiusKm(GeoCell cell, double radiusKm)
        => RadiusKey(radiusKm) + HalfDiagonalKm(cell);

    // Rows within kth + 2h of the centre hold the k nearest rows of every point of the cell (triangle inequality).
    public static double NearestCandidateRadiusKm(GeoCell cell, double kthDistanceKm)
        => kthDistanceKm * (1 + MetricTolerance) + 2 * HalfDiagonalKm(cell);

    // Exact radius answer for one caller: d <= r, paid rank first, then nearest; ties keep input order.
    public static List<GeoHit> WithinRadius(IReadOnlyList<GeoPoint> candidates, double lat, double lng, double radiusKm)
    {
        var hits = new List<(GeoHit Hit, short Rank)>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var d = GeoMath.HaversineKm(lat, lng, c.Lat, c.Lng);
            if (d <= radiusKm) hits.Add((new GeoHit(i, d), c.Rank));
        }
        return hits
            .OrderByDescending(h => h.Rank)
            .ThenBy(h => h.Hit.DistanceKm)
            .ThenBy(h => h.Hit.Index)
            .Select(h => h.Hit)
            .ToList();
    }

    // Exact k nearest for one caller, then shown paid rank first, then nearest (same as the KNN endpoint).
    public static List<GeoHit> Nearest(IReadOnlyList<GeoPoint> candidates, double lat, double lng, int count)
    {
        if (count <= 0) return [];
        return candidates
            .Select((c, i) => (Hit: new GeoHit(i, GeoMath.HaversineKm(lat, lng, c.Lat, c.Lng)), c.Rank))
            .OrderBy(x => x.Hit.DistanceKm)
            .ThenBy(x => x.Hit.Index)
            .Take(count)
            .OrderByDescending(x => x.Rank)
            .ThenBy(x => x.Hit.DistanceKm)
            .ThenBy(x => x.Hit.Index)
            .Select(x => x.Hit)
            .ToList();
    }

    // Index of the nearest point with coordinates; first wins a tie; null when no point has coordinates.
    public static int? NearestIndex(IReadOnlyList<(double? Lat, double? Lng)> points, double lat, double lng)
    {
        int? best = null;
        var bestKm = double.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].Lat is not double pLat || points[i].Lng is not double pLng) continue;
            var d = GeoMath.HaversineKm(lat, lng, pLat, pLng);
            if (d < bestKm)
            {
                bestKm = d;
                best = i;
            }
        }
        return best;
    }

    public static double MaxDistanceKm(IEnumerable<(double Lat, double Lng)> points, double lat, double lng)
        => points.Select(p => GeoMath.HaversineKm(lat, lng, p.Lat, p.Lng)).DefaultIfEmpty(0).Max();
}

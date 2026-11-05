using RentNearBy.Core.Utils;

namespace RentNearBy.Core.Models;

// City identity inside one district: the same folded name, or the same compact name (spaces and
// punctuation dropped: "Bhim Tal" = "Bhimtal") within SamePlaceKm. Nearest wins.
public static class CityNameMatcher
{
    public const double SamePlaceKm = 2.0;

    public readonly record struct Candidate(string Name, double? Lat, double? Lng);

    public static string CompactKey(string? name)
    {
        var folded = SearchKeys.Fold(name);
        return folded.Length == 0 ? string.Empty : folded.Replace(" ", string.Empty);
    }

    // Identity key for seed dedupe; a name with no Latin letters keys on its trimmed lower form.
    public static string IdentityKey(string name)
    {
        var compact = CompactKey(name);
        return compact.Length > 0 ? compact : name.Trim().ToLowerInvariant();
    }

    public static bool SamePlace(double latA, double lngA, double latB, double lngB)
        => GeoMath.HaversineKm(latA, lngA, latB, lngB) <= SamePlaceKm;

    // Index of the matching candidate or null. 1) Same folded name: the nearest one (no coordinates: the first).
    // 2) Same compact name within SamePlaceKm of the pin: the nearest one.
    public static int? Match(IReadOnlyList<Candidate> cities, string name, double? lat, double? lng)
    {
        var folded = SearchKeys.Fold(name);
        if (folded.Length == 0) return null;

        var exact = Best(cities, c => SearchKeys.Fold(c.Name) == folded, lat, lng, maxKm: null);
        if (exact != null) return exact;

        if (lat is null || lng is null) return null;
        var compact = folded.Replace(" ", string.Empty);
        return Best(cities, c => CompactKey(c.Name) == compact, lat, lng, maxKm: SamePlaceKm);
    }

    private static int? Best(IReadOnlyList<Candidate> cities, Func<Candidate, bool> nameMatches, double? lat, double? lng, double? maxKm)
    {
        int? best = null;
        var bestKm = double.MaxValue;
        for (var i = 0; i < cities.Count; i++)
        {
            var c = cities[i];
            if (!nameMatches(c)) continue;
            double km;
            if (lat is double pLat && lng is double pLng && c.Lat is double cLat && c.Lng is double cLng)
                km = GeoMath.HaversineKm(pLat, pLng, cLat, cLng);
            else if (maxKm != null)
                continue;
            else
                km = double.MaxValue;

            if (maxKm != null && km > maxKm) continue;
            if (best == null || km < bestKm)
            {
                best = i;
                bestKm = km;
            }
        }
        return best;
    }
}

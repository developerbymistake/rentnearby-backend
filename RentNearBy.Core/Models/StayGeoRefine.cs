using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Core.Models;

// Stay nearby/nearest over a cached cell candidate set (GeoCacheKey): each caller gets the exact cut and distances of its own point.
public static class StayGeoRefine
{
    public static List<StayNearbyDto> WithinRadius(IReadOnlyList<StayNearbyDto> candidates, double lat, double lng, double radiusKm)
        => Pick(candidates, GeoCacheKey.WithinRadius(Points(candidates), lat, lng, radiusKm));

    public static List<StayNearbyDto> Nearest(IReadOnlyList<StayNearbyDto> candidates, double lat, double lng, int count)
        => Pick(candidates, GeoCacheKey.Nearest(Points(candidates), lat, lng, count));

    // Map sheets download small images: Cloudinary urls get the thumbnail transform, others stay as they are.
    public static List<StayNearbyDto> WithMapThumbs(List<StayNearbyDto> items)
    {
        foreach (var s in items) s.ThumbnailUrl = RentNearBy.Core.Utils.CloudinaryThumb.Apply(s.ThumbnailUrl, RentNearBy.Core.Utils.CloudinaryThumb.StayMap);
        return items;
    }

    // Nearest within a distance cap (no district): the k nearest of the caller, those farther than capKm dropped.
    public static List<StayNearbyDto> NearestWithin(IReadOnlyList<StayNearbyDto> candidates, double lat, double lng, int count, double capKm)
        => Nearest(candidates, lat, lng, count).Where(s => s.DistanceKm <= capKm).ToList();

    // Cap for the KNN from the cell centre: a stay within capKm of any point of the cell is within this of the centre
    // (2h for the cell, 1% for sphere vs spheroid).
    public static double CentreCapKm(GeoCell cell, double capKm)
        => capKm * 1.01 + 2 * GeoCacheKey.HalfDiagonalKm(cell);

    // Radius around the cell centre that holds the k nearest stays of every point of the cell (fewer than k rows from the
    // KNN = every stay, all inside the same radius); null = no stay at all.
    public static double? NearestCandidateRadiusKm(GeoCell cell, IReadOnlyList<StayNearbyDto> knnFromCentre)
    {
        if (knnFromCentre.Count == 0) return null;
        var kth = GeoCacheKey.MaxDistanceKm(knnFromCentre.Select(s => ((double)s.Latitude, (double)s.Longitude)), cell.CenterLat, cell.CenterLng);
        return GeoCacheKey.NearestCandidateRadiusKm(cell, kth);
    }

    private static List<GeoPoint> Points(IReadOnlyList<StayNearbyDto> candidates)
        => candidates.Select(s => new GeoPoint((double)s.Latitude, (double)s.Longitude, s.PriorityRank)).ToList();

    private static List<StayNearbyDto> Pick(IReadOnlyList<StayNearbyDto> candidates, List<GeoHit> hits)
    {
        var result = new List<StayNearbyDto>(hits.Count);
        foreach (var hit in hits)
        {
            var item = candidates[hit.Index];
            item.DistanceKm = hit.DistanceKm;
            result.Add(item);
        }
        return result;
    }
}

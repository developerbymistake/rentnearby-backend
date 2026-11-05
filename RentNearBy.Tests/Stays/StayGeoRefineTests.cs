using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Tests.Stays;

// Finding 25: nearby and nearest cache the candidate set of a grid cell; every caller gets its own exact cut and distances.
public class StayGeoRefineTests
{
    private static readonly Guid District = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly GeoCell Cell = GeoCacheKey.Cell(29.3805, 79.4605);
    private static readonly (double Lat, double Lng) A = (29.3809, 79.4609);
    private static readonly (double Lat, double Lng) B = (29.3801, 79.4601);

    private static StayNearbyDto Stay(double lat, double lng, short rank = 0) => new()
    {
        Id = Guid.NewGuid(), Latitude = (decimal)lat, Longitude = (decimal)lng, PriorityRank = rank,
    };

    // What GetNearbyAsync returns for the cell centre and a radius (repository cut, paid rank then nearest).
    private static List<StayNearbyDto> Fetch(IEnumerable<StayNearbyDto> all, double radiusKm)
        => all.Where(s => GeoMath.HaversineKm(Cell.CenterLat, Cell.CenterLng, (double)s.Latitude, (double)s.Longitude) <= radiusKm).ToList();

    private static List<StayNearbyDto> Grid(int seed, int count, double spreadDeg)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => Stay(
                Cell.CenterLat + (random.NextDouble() * 2 - 1) * spreadDeg,
                Cell.CenterLng + (random.NextDouble() * 2 - 1) * spreadDeg,
                (short)(random.Next(4) == 0 ? 5 : 0)))
            .ToList();
    }

    [Fact]
    public void Two_callers_of_one_cell_share_the_key_but_each_gets_its_own_cut_and_distance()
    {
        Assert.Equal(Cell, GeoCacheKey.Cell(A.Lat, A.Lng));
        Assert.Equal(Cell, GeoCacheKey.Cell(B.Lat, B.Lng));
        Assert.Equal(StayCache.NearbyKey(District, null, 5, GeoCacheKey.Cell(A.Lat, A.Lng)), StayCache.NearbyKey(District, null, 5, Cell));

        var edge = Stay(B.Lat - 4.98 / 111.195, B.Lng);
        Assert.True(GeoMath.HaversineKm(A.Lat, A.Lng, (double)edge.Latitude, (double)edge.Longitude) > 5.0);
        var candidates = Fetch([edge], GeoCacheKey.CandidateRadiusKm(Cell, 5));

        var forB = StayGeoRefine.WithinRadius(Fetch([edge], GeoCacheKey.CandidateRadiusKm(Cell, 5)), B.Lat, B.Lng, 5);
        var forA = StayGeoRefine.WithinRadius(candidates, A.Lat, A.Lng, 5);

        var hit = Assert.Single(forB);
        Assert.Equal(GeoMath.HaversineKm(B.Lat, B.Lng, (double)edge.Latitude, (double)edge.Longitude), hit.DistanceKm, 9);
        Assert.Empty(forA);
    }

    [Fact]
    public void Nearby_from_the_cell_set_equals_the_exact_answer_for_any_point_of_the_cell()
    {
        var rows = Grid(25, 300, 0.08);
        var random = new Random(52);
        foreach (var radius in new[] { 1.0, 2.5, 5.0 })
        {
            var candidates = Fetch(rows, GeoCacheKey.CandidateRadiusKm(Cell, radius));
            for (var i = 0; i < 40; i++)
            {
                var lat = 29.380 + random.NextDouble() * 0.001;
                var lng = 79.460 + random.NextDouble() * 0.001;
                var exact = rows
                    .Select(s => (Stay: s, D: GeoMath.HaversineKm(lat, lng, (double)s.Latitude, (double)s.Longitude)))
                    .Where(x => x.D <= radius)
                    .OrderByDescending(x => x.Stay.PriorityRank).ThenBy(x => x.D)
                    .Select(x => x.Stay.Id)
                    .ToList();
                Assert.Equal(exact, StayGeoRefine.WithinRadius(candidates, lat, lng, radius).Select(s => s.Id).ToList());
            }
        }
    }

    [Fact]
    public void Nearest_from_the_cell_set_equals_the_exact_k_nearest_for_any_point_of_the_cell()
    {
        var rows = Grid(7, 200, 0.05);
        var random = new Random(11);
        foreach (var count in new[] { 1, 5, 10 })
        {
            var knn = rows
                .OrderBy(s => GeoMath.HaversineKm(Cell.CenterLat, Cell.CenterLng, (double)s.Latitude, (double)s.Longitude))
                .Take(count)
                .ToList();
            var candidates = Fetch(rows, StayGeoRefine.NearestCandidateRadiusKm(Cell, knn)!.Value);
            for (var i = 0; i < 40; i++)
            {
                var lat = 29.380 + random.NextDouble() * 0.001;
                var lng = 79.460 + random.NextDouble() * 0.001;
                var exact = rows
                    .Select(s => (Stay: s, D: GeoMath.HaversineKm(lat, lng, (double)s.Latitude, (double)s.Longitude)))
                    .OrderBy(x => x.D).Take(count)
                    .OrderByDescending(x => x.Stay.PriorityRank).ThenBy(x => x.D)
                    .Select(x => x.Stay.Id)
                    .ToList();
                Assert.Equal(exact, StayGeoRefine.Nearest(candidates, lat, lng, count).Select(s => s.Id).ToList());
            }
        }
    }

    [Fact]
    public void Nearest_edges_empty_and_fewer_than_count()
    {
        Assert.Null(StayGeoRefine.NearestCandidateRadiusKm(Cell, []));
        Assert.Empty(StayGeoRefine.Nearest([], A.Lat, A.Lng, 5));
        Assert.Empty(StayGeoRefine.WithinRadius([], A.Lat, A.Lng, 5));

        var two = new List<StayNearbyDto> { Stay(29.40, 79.46), Stay(29.30, 79.46) };
        var radius = StayGeoRefine.NearestCandidateRadiusKm(Cell, two)!.Value;
        Assert.Equal(2, Fetch(two, radius).Count);
        Assert.Equal(2, StayGeoRefine.Nearest(two, A.Lat, A.Lng, 5).Count);
    }

    [Fact]
    public void Ties_keep_the_cached_order()
    {
        var x = Stay(29.39, 79.4609);
        var y = Stay(29.39, 79.4609);
        var list = StayGeoRefine.WithinRadius([x, y], A.Lat, A.Lng, 5);
        Assert.Equal(new[] { x.Id, y.Id }, list.Select(s => s.Id));
    }
}

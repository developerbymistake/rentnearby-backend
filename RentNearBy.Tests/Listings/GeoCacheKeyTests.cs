namespace RentNearBy.Tests.Listings;

public class GeoCacheKeyTests
{
    private static readonly GeoCell Cell = GeoCacheKey.Cell(29.3805, 79.4505);
    private static readonly (double Lat, double Lng) A = (29.3809, 79.4509);
    private static readonly (double Lat, double Lng) B = (29.3801, 79.4501);

    // What the repository returns for a cell: every row within the widened radius of the cell centre.
    private static List<RentNearBy.Core.Models.GeoPoint> CellCandidates(IEnumerable<RentNearBy.Core.Models.GeoPoint> rows, double radiusKm)
        => rows.Where(r => GeoMath.HaversineKm(Cell.CenterLat, Cell.CenterLng, r.Lat, r.Lng) <= radiusKm).ToList();

    private static List<RentNearBy.Core.Models.GeoPoint> Grid(int seed, int count, double spreadDeg)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => new RentNearBy.Core.Models.GeoPoint(
                Cell.CenterLat + (random.NextDouble() * 2 - 1) * spreadDeg,
                Cell.CenterLng + (random.NextDouble() * 2 - 1) * spreadDeg,
                (short)(random.Next(4) == 0 ? 5 : 0)))
            .ToList();
    }

    [Fact]
    public void Cell_FloorsToTheGrid_AndCentreIsInside()
    {
        Assert.Equal(new GeoCell(29380, 79450, 3), Cell);
        Assert.Equal("3:29380:79450", Cell.Key);
        Assert.Equal(29.3805, Cell.CenterLat, 9);
        Assert.Equal(79.4505, Cell.CenterLng, 9);
        Assert.Equal(Cell, GeoCacheKey.Cell(A.Lat, A.Lng));
        Assert.Equal(Cell, GeoCacheKey.Cell(B.Lat, B.Lng));
    }

    [Fact]
    public void HalfDiagonal_BoundsEveryCorner()
    {
        var h = GeoCacheKey.HalfDiagonalKm(Cell);
        Assert.InRange(h, 0.07, 0.09);
        foreach (var (lat, lng) in new[] { (29.380, 79.450), (29.381, 79.451), (29.380, 79.451), (29.381, 79.450) })
            Assert.True(GeoMath.HaversineKm(Cell.CenterLat, Cell.CenterLng, lat, lng) <= h);
    }

    [Theory]
    [InlineData(5.0, 5.0)]
    [InlineData(5.04, 5.1)]
    [InlineData(1, 1.0)]
    [InlineData(49.95, 50.0)]
    public void RadiusKey_RoundsUp(double radius, double expected)
        => Assert.Equal(expected, GeoCacheKey.RadiusKey(radius), 9);

    // Finding 14: two callers 0.1 km apart share the key, but a row 4.98 km from B is more than 5 km from A.
    [Fact]
    public void WithinRadius_SameCell_EachCallerGetsItsOwnExactCut()
    {
        var r = new RentNearBy.Core.Models.GeoPoint(B.Lat - 4.98 / 111.195, B.Lng, 0);
        Assert.True(GeoMath.HaversineKm(B.Lat, B.Lng, r.Lat, r.Lng) <= 5.0);
        Assert.True(GeoMath.HaversineKm(A.Lat, A.Lng, r.Lat, r.Lng) > 5.0);

        var candidates = CellCandidates([r], GeoCacheKey.CandidateRadiusKm(Cell, 5.0));
        Assert.Single(candidates);

        var forB = GeoCacheKey.WithinRadius(candidates, B.Lat, B.Lng, 5.0);
        var forA = GeoCacheKey.WithinRadius(candidates, A.Lat, A.Lng, 5.0);
        Assert.Single(forB);
        Assert.Equal(GeoMath.HaversineKm(B.Lat, B.Lng, r.Lat, r.Lng), forB[0].DistanceKm, 9);
        Assert.Empty(forA);
    }

    [Fact]
    public void WithinRadius_FromCellCandidates_EqualsBruteForce_ForAnyPointOfTheCell()
    {
        var rows = Grid(14, 400, 0.08);
        var random = new Random(36);
        foreach (var radius in new[] { 1.0, 2.5, 5.0 })
        {
            var candidates = CellCandidates(rows, GeoCacheKey.CandidateRadiusKm(Cell, radius));
            for (var i = 0; i < 50; i++)
            {
                var lat = 29.380 + random.NextDouble() * 0.001;
                var lng = 79.450 + random.NextDouble() * 0.001;
                var cached = GeoCacheKey.WithinRadius(candidates, lat, lng, radius).Select(h => candidates[h.Index]).ToList();
                var exact = GeoCacheKey.WithinRadius(rows, lat, lng, radius).Select(h => rows[h.Index]).ToList();
                Assert.Equal(exact, cached);
            }
        }
    }

    [Fact]
    public void WithinRadius_PaidRankFirstThenNearest()
    {
        var near = new RentNearBy.Core.Models.GeoPoint(B.Lat + 0.001, B.Lng, 0);
        var far = new RentNearBy.Core.Models.GeoPoint(B.Lat + 0.02, B.Lng, 5);
        var hits = GeoCacheKey.WithinRadius([near, far], B.Lat, B.Lng, 5);
        Assert.Equal(new[] { 1, 0 }, hits.Select(h => h.Index));
    }

    [Fact]
    public void WithinRadius_Empty_Empty()
        => Assert.Empty(GeoCacheKey.WithinRadius([], B.Lat, B.Lng, 5));

    [Fact]
    public void Nearest_FromCellCandidates_EqualsBruteForce_ForAnyPointOfTheCell()
    {
        var rows = Grid(21, 300, 0.1);
        var random = new Random(82);
        foreach (var count in new[] { 1, 5, 10 })
        {
            var knnFromCentre = GeoCacheKey.Nearest(rows.Select(r => r with { Rank = 0 }).ToList(), Cell.CenterLat, Cell.CenterLng, count);
            var kth = knnFromCentre.Max(h => h.DistanceKm);
            var candidates = CellCandidates(rows, GeoCacheKey.NearestCandidateRadiusKm(Cell, kth));
            for (var i = 0; i < 50; i++)
            {
                var lat = 29.380 + random.NextDouble() * 0.001;
                var lng = 79.450 + random.NextDouble() * 0.001;
                var cached = GeoCacheKey.Nearest(candidates, lat, lng, count).Select(h => candidates[h.Index]).ToList();
                var exact = GeoCacheKey.Nearest(rows, lat, lng, count).Select(h => rows[h.Index]).ToList();
                Assert.Equal(exact, cached);
            }
        }
    }

    [Fact]
    public void Nearest_TakesKNearestThenRanksPaidFirst()
    {
        var p0 = new RentNearBy.Core.Models.GeoPoint(B.Lat + 0.001, B.Lng, 0);
        var p1 = new RentNearBy.Core.Models.GeoPoint(B.Lat + 0.002, B.Lng, 5);
        var p2 = new RentNearBy.Core.Models.GeoPoint(B.Lat + 0.5, B.Lng, 9);
        var hits = GeoCacheKey.Nearest([p0, p1, p2], B.Lat, B.Lng, 2);
        Assert.Equal(new[] { 1, 0 }, hits.Select(h => h.Index));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nearest_NonPositiveCount_Empty(int count)
        => Assert.Empty(GeoCacheKey.Nearest([new RentNearBy.Core.Models.GeoPoint(B.Lat, B.Lng, 0)], B.Lat, B.Lng, count));

    [Fact]
    public void NearestIndex_SkipsNullCoordinates_FirstWinsTie()
    {
        var points = new List<(double? Lat, double? Lng)> { (null, null), (29.40, 79.45), (29.40, 79.45), (29.60, 79.45) };
        Assert.Equal(1, GeoCacheKey.NearestIndex(points, 29.39, 79.45));
    }

    [Fact]
    public void NearestIndex_NoCoordinates_Null()
        => Assert.Null(GeoCacheKey.NearestIndex([(null, null)], 29.39, 79.45));

    // Finding 10/47: the nearest city is computed from the caller's own point, never from a cell neighbour.
    [Fact]
    public void NearestIndex_TwoCallersOfOneOldContextCell_GetTheirOwnCity()
    {
        var cities = new List<(double? Lat, double? Lng)> { (29.4985, 79.4800), (29.5050, 79.4950) };
        Assert.Equal(0, GeoCacheKey.NearestIndex(cities, 29.496, 79.486));
        Assert.Equal(1, GeoCacheKey.NearestIndex(cities, 29.504, 79.494));
    }
}

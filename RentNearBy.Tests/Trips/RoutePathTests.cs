namespace RentNearBy.Tests.Trips;

public class RoutePathTests
{
    private static TripSpot Spot(string id, double lat, double lon, int visit = 90)
        => new() { Id = id, Name = id, Latitude = lat, Longitude = lon, Category = "attraction", VisitMinutes = visit };

    // Road minutes from straight distance (2.4 min per km, hills), symmetric.
    private static RoadMatrix Roads(IReadOnlyList<TripSpot> spots)
    {
        var n = spots.Count;
        var minutes = new int[n, n];
        var km = new double[n, n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
        {
            if (i == j) continue;
            var d = GeoMath.HaversineKm(spots[i].Latitude, spots[i].Longitude, spots[j].Latitude, spots[j].Longitude);
            minutes[i, j] = (int)Math.Ceiling(d * 2.4);
            km[i, j] = d;
        }
        return new RoadMatrix(minutes, km, new bool[n, n]);
    }

    private static string Area(string id) => id[..id.IndexOf('-')];

    // Nainital (4), Kainchi (1) and Bhimtal (4), given to the planner in a deliberately mixed order.
    private static TripSpot[] NainitalTrip() =>
    [
        Spot("bhimtal-lake", 29.3470, 79.5640), Spot("nainital-lake", 29.3919, 79.4542), Spot("kainchi-dham", 29.4366, 79.5128),
        Spot("nainital-snowview", 29.3990, 79.4510), Spot("bhimtal-aquarium", 29.3480, 79.5650), Spot("nainital-mall", 29.3930, 79.4560),
        Spot("bhimtal-sattal", 29.3550, 79.5460), Spot("nainital-zoo", 29.3900, 79.4650), Spot("bhimtal-naukuchia", 29.3190, 79.5580),
    ];

    [Fact]
    public void Order_VisitsEachAreaOnce_NoDoublingBack()
    {
        var spots = NainitalTrip();
        var order = RoutePath.Order(Roads(spots).Minutes!, spots, 29.3919, 79.4542);

        Assert.Equal(spots.Length, order.Distinct().Count());
        var areas = order.Select(i => Area(spots[i].Id)).ToList();
        var changes = Enumerable.Range(1, areas.Count - 1).Count(k => areas[k] != areas[k - 1]);
        Assert.True(changes <= 2, string.Join(" > ", areas));
    }

    [Fact]
    public void Order_IsNeverLongerThanNearestNeighbour()
    {
        var spots = NainitalTrip();
        var m = Roads(spots).Minutes!;
        long Length(int[] o) => Enumerable.Range(0, o.Length - 1).Sum(k => (long)m[o[k], o[k + 1]]);

        var path = RoutePath.Order(m, spots, 29.3919, 79.4542);
        for (var start = 0; start < spots.Length; start++)
        {
            var used = new bool[spots.Length];
            var nn = new List<int> { start };
            used[start] = true;
            while (nn.Count < spots.Length)
            {
                var from = nn[^1];
                var next = Enumerable.Range(0, spots.Length).Where(i => !used[i]).OrderBy(i => m[from, i]).First();
                nn.Add(next);
                used[next] = true;
            }
            Assert.True(Length(path) <= Length(nn.ToArray()), $"start {start}");
        }
    }

    [Fact]
    public void Order_IsDeterministic()
    {
        var spots = NainitalTrip();
        var m = Roads(spots).Minutes!;
        Assert.Equal(RoutePath.Order(m, spots, 29.3919, 79.4542), RoutePath.Order(m, spots, 29.3919, 79.4542));
    }

    [Fact]
    public void Split_KeepsEveryAreaTogetherAcrossDays()
    {
        var spots = NainitalTrip();
        var result = DaySplitter.Split(new SplitInput(spots, 29.3919, 79.4542, 450, new DateOnly(2026, 11, 2), 14), Roads(spots));

        var areas = result.Days.SelectMany(d => d.Spots).Select(s => Area(s.Id)).ToList();
        Assert.Equal(spots.Length, areas.Count);
        var changes = Enumerable.Range(1, areas.Count - 1).Count(k => areas[k] != areas[k - 1]);
        Assert.True(changes <= 2, string.Join(" > ", areas));
        Assert.True(result.Days.Count >= 2);
    }

    [Fact]
    public void Order_SinglePlace_AndPair()
    {
        var one = new[] { Spot("a-1", 29.39, 79.45) };
        Assert.Equal(new[] { 0 }, RoutePath.Order(new int[1, 1], one, 29.39, 79.45));

        var two = new[] { Spot("far-1", 29.50, 79.60), Spot("near-1", 29.391, 79.451) };
        var m = Roads(two).Minutes!;
        Assert.Equal(new[] { 1, 0 }, RoutePath.Order(m, two, 29.39, 79.45));
    }
}

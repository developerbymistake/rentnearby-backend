namespace RentNearBy.Tests.Trips;

// Vectors of the algorithm audit (findings 1-4): Prins' Split DP over the RoutePath tour.
public class DaySplitTests
{
    private const int Limit = 450;
    private static readonly DateOnly Monday = new(2026, 11, 2);

    // Nainital N1..N4 = 0..3, Bhimtal B1..B4 = 4..7, Kainchi K = 8, stay (anchor) = 9.
    // In-area 10, N-B 40, B-K 30, N-K 60; stay to N 5, to B 40, to K 35.
    private static int[,] Hills()
    {
        static int Area(int i) => i <= 3 ? 0 : i <= 7 ? 1 : i == 8 ? 2 : 3;
        var m = new int[10, 10];
        for (var i = 0; i < 10; i++)
        for (var j = 0; j < 10; j++)
        {
            if (i == j) continue;
            var (a, b) = (Math.Min(Area(i), Area(j)), Math.Max(Area(i), Area(j)));
            m[i, j] = (a, b) switch
            {
                _ when a == b => 10,
                (0, 1) => 40,
                (1, 2) => 30,
                (0, 2) => 60,
                (0, 3) => 5,
                (1, 3) => 40,
                (2, 3) => 35,
                _ => 99,
            };
        }
        return m;
    }

    private static int[] Visits(int n, int visit = 60) => Enumerable.Repeat(visit, n).ToArray();

    // Path of n places, every leg `leg` minutes.
    private static int[,] Line(int n, int leg)
    {
        var m = new int[n, n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            if (i != j) m[i, j] = leg;
        return m;
    }

    private static int[][] Days(DaySplitResult r) => r.Days.Select(d => d.ToArray()).ToArray();

    [Fact]
    public void Finding1_CutsAtTheAreaBoundary_NotWhereTheLimitIsCrossed()
    {
        var result = DaySplit.Solve(new DaySplitInput(Enumerable.Range(0, 9).ToArray(), Visits(10), Hills(), Limit, 14));

        Assert.Equal(new[] { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7, 8 } }, Days(result));
        Assert.Equal(90, result.Drive);
        Assert.Equal(0, result.Overflow);
    }

    [Fact]
    public void Finding2_AnchorLegsCountInTheDay()
    {
        var result = DaySplit.Solve(new DaySplitInput(Enumerable.Range(0, 9).ToArray(), Visits(10), Hills(), Limit, 14, Anchor: 9));

        Assert.Equal(new[] { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7, 8 } }, Days(result));
        Assert.Equal(5 + 30 + 5 + 40 + 60 + 35, result.Drive);
    }

    [Fact]
    public void Finding2_AnchorFarAway_NeedsAnotherDay()
    {
        var m = new int[4, 4];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            if (i != j) m[i, j] = 10;
        for (var i = 0; i < 3; i++) { m[i, 3] = 60; m[3, i] = 60; }
        var visits = new[] { 120, 120, 120, 0 };

        var plain = DaySplit.Solve(new DaySplitInput([0, 1, 2], visits, m, Limit, 14));
        var anchored = DaySplit.Solve(new DaySplitInput([0, 1, 2], visits, m, Limit, 14, Anchor: 3));

        Assert.Single(plain.Days);
        Assert.Equal(2, anchored.Days.Length);
        Assert.Equal(0, anchored.Overflow);
    }

    [Fact]
    public void Finding3_ClosedPlace_NoSkipAndWrap()
    {
        var closed = new bool[8, 14];
        closed[1, 0] = true;

        var result = DaySplit.Solve(new DaySplitInput(Enumerable.Range(0, 8).ToArray(), Visits(8), Hills(), Limit, 14, Closed: closed));

        Assert.Equal(new[] { new[] { 7, 6, 5, 4 }, new[] { 1, 3, 2, 0 } }, Days(result));
        Assert.Equal(0, result.Violations);
        Assert.Equal(60, result.Drive);
    }

    [Fact]
    public void Finding4_CappedDays_SpreadTheOverflow()
    {
        var result = DaySplit.Solve(new DaySplitInput(Enumerable.Range(0, 14).ToArray(), Visits(14), Line(14, 10), Limit, 2));

        Assert.Equal(new[] { 7, 7 }, result.Days.Select(d => d.Count).ToArray());
        Assert.Equal(30, result.Overflow);
    }

    [Fact]
    public void Balanced_FewestDaysThenEvenDays()
    {
        var result = DaySplit.Solve(new DaySplitInput(Enumerable.Range(0, 14).ToArray(), Visits(14), Line(14, 10), Limit, 14));

        Assert.Equal(new[] { 5, 5, 4 }, result.Days.Select(d => d.Count).ToArray());
        Assert.Equal(0, result.Overflow);
    }

    [Fact]
    public void Empty_NoDays()
    {
        var result = DaySplit.Solve(new DaySplitInput([], [], new int[0, 0], Limit, 14));

        Assert.Empty(result.Days);
    }

    [Fact]
    public void SinglePlaceLongerThanLimit_OwnDayNoOverflow()
    {
        var result = DaySplit.Solve(new DaySplitInput([0], [600], new int[1, 1], Limit, 14));

        Assert.Equal(new[] { new[] { 0 } }, Days(result));
        Assert.Equal(0, result.Overflow);
    }

    [Fact]
    public void ReservedDayOneTooShort_DayOneFree()
    {
        var result = DaySplit.Solve(new DaySplitInput([0, 1], Visits(2, 30), Line(2, 5), Limit, 14, Reserved: 400));

        Assert.Equal(2, result.Days.Length);
        Assert.Empty(result.Days[0]);
        Assert.Equal(new[] { 0, 1 }, result.Days[1]);
    }

    [Fact]
    public void ClosedEveryDay_CountedAsViolation()
    {
        var closed = new bool[2, 2];
        closed[1, 0] = closed[1, 1] = true;

        var result = DaySplit.Solve(new DaySplitInput([0, 1], Visits(2), Line(2, 20), Limit, 2, Closed: closed));

        Assert.Equal(1, result.Violations);
        Assert.Single(result.Days);
    }

    [Fact]
    public void Ties_AreDeterministic()
    {
        var input = new DaySplitInput(Enumerable.Range(0, 8).ToArray(), Visits(8), Line(8, 10), Limit, 14);

        Assert.Equal(Days(DaySplit.Solve(input)), Days(DaySplit.Solve(input)));
    }

    [Fact]
    public void Anchor_IsValid_BothOrNeitherAndNearTheCity()
    {
        Assert.True(TripAnchor.IsValid(null, null, 29.39, 79.45, 200));
        Assert.False(TripAnchor.IsValid(29.39, null, 29.39, 79.45, 200));
        Assert.False(TripAnchor.IsValid(double.NaN, 79.45, 29.39, 79.45, 200));
        Assert.False(TripAnchor.IsValid(19.07, 72.87, 29.39, 79.45, 200));
        Assert.True(TripAnchor.IsValid(29.35, 79.56, 29.39, 79.45, 200));
        Assert.Null(TripAnchor.Spot(null, 79.45));
    }

    [Fact]
    public void Anchor_Embed_KeepsInnerCellsAndMarksTheRest()
    {
        var inner = new RoadMatrix(new[,] { { 0, 7 }, { 8, 0 } }, new[,] { { 0.0, 1.5 }, { 1.6, 0.0 } }, new bool[2, 2]);

        var embedded = TripAnchor.Embed(inner, 3)!;

        Assert.Equal(7, embedded.Minutes![0, 1]);
        Assert.False(embedded.IsEstimate[1, 0]);
        Assert.True(embedded.IsEstimate[0, 2]);
        Assert.True(embedded.IsEstimate[2, 1]);
        Assert.Null(TripAnchor.Embed(null, 3));
    }

    private static TripSpot Spot(string id, double lat, double lon, int[]? closed = null)
        => new() { Id = id, Name = id, Latitude = lat, Longitude = lon, Category = "attraction", VisitMinutes = 60, ClosedDays = closed };

    private static TripSpot[] HillSpots(int[]? n2Closed = null) =>
    [
        Spot("n1", 29.3919, 79.4542), Spot("n2", 29.3930, 79.4560, n2Closed), Spot("n3", 29.3950, 79.4580), Spot("n4", 29.3970, 79.4600),
        Spot("b1", 29.3470, 79.5640), Spot("b2", 29.3480, 79.5650), Spot("b3", 29.3490, 79.5660), Spot("b4", 29.3500, 79.5670),
        Spot("k", 29.4366, 79.5128),
    ];

    // The first n rows of Hills(); n = 10 includes the stay.
    private static RoadMatrix HillRoads(int n)
    {
        var all = Hills();
        var minutes = new int[n, n];
        var km = new double[n, n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            if (i != j) { minutes[i, j] = all[i, j]; km[i, j] = 1; }
        return new RoadMatrix(minutes, km, new bool[n, n]);
    }

    private static string Area(string id) => id[..1];

    [Fact]
    public void Splitter_Finding1_EachAreaOnOneDay()
    {
        var spots = HillSpots();

        var result = DaySplitter.Split(new SplitInput(spots, 29.3919, 79.4542, Limit, null, 14), HillRoads(9));

        Assert.Equal(2, result.Days.Count);
        Assert.Equal(new[] { "n" }, result.Days[0].Spots.Select(s => Area(s.Id)).Distinct());
        Assert.Equal(new[] { "b", "k" }, result.Days[1].Spots.Select(s => Area(s.Id)).Distinct().Order());
        Assert.Equal(270, result.Days[0].TotalMinutes);
        Assert.Equal(360, result.Days[1].TotalMinutes);
    }

    [Fact]
    public void Splitter_Finding2_AnchorLegsInDriveLegsAndLimit()
    {
        var spots = HillSpots();
        var anchor = TripAnchor.Spot(29.3925, 79.4550)!;

        var result = DaySplitter.Split(new SplitInput(spots, 29.3919, 79.4542, Limit, null, 14), HillRoads(10), anchor);

        var day2 = result.Days[1];
        Assert.Equal(435, day2.TotalMinutes);
        Assert.False(day2.OverLimit);
        Assert.Equal(TripAnchor.Id, day2.Legs[0].FromId);
        Assert.Equal(TripAnchor.Id, day2.Legs[^1].ToId);
        Assert.Equal(day2.Legs.Sum(l => l.Minutes), day2.DriveMinutes);
        Assert.Equal(280, result.Days[0].TotalMinutes);
    }

    [Fact]
    public void Splitter_Finding3_NoAreaOnTwoDays()
    {
        var spots = HillSpots([Monday.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)Monday.DayOfWeek]).Take(8).ToArray();

        var result = DaySplitter.Split(new SplitInput(spots, 29.3919, 79.4542, Limit, Monday, 14), HillRoads(8));

        Assert.Equal(2, result.Days.Count);
        Assert.All(result.Days, d => Assert.Single(d.Spots.Select(s => Area(s.Id)).Distinct()));
        Assert.DoesNotContain(result.Days[0].Spots, s => s.Id == "n2");
        Assert.DoesNotContain(result.Warnings, w => w.Code == TripWarningCodes.ClosedDay);
    }

    [Fact]
    public void Splitter_Finding4_FourteenPlacesTwoDays_SevenAndSeven()
    {
        var spots = Enumerable.Range(0, 14).Select(i => Spot($"s{i}", 29.39 + i * 0.001, 79.45)).ToArray();
        var minutes = Line(14, 10);
        var matrix = new RoadMatrix(minutes, new double[14, 14], new bool[14, 14]);

        var result = DaySplitter.Split(new SplitInput(spots, 29.39, 79.45, Limit, null, 2), matrix);

        Assert.Equal(new[] { 480, 480 }, result.Days.Select(d => d.TotalMinutes).ToArray());
        Assert.All(result.Days, d => Assert.True(d.OverLimit));
        Assert.Contains(result.Warnings, w => w.Code == TripWarningCodes.TooManyDays);
    }

    [Fact]
    public void Recalculate_WithAnchor_KeepsOrderAndAddsBaseLegs()
    {
        var spots = HillSpots().Take(2).ToList();
        var anchor = TripAnchor.Spot(29.3925, 79.4550)!;
        var days = new List<(int Day, List<TripSpot> Spots)> { (1, new List<TripSpot> { spots[1], spots[0] }) };
        var m = new int[3, 3];
        m[0, 1] = m[1, 0] = 10;
        m[2, 0] = m[0, 2] = m[2, 1] = m[1, 2] = 5;
        var matrix = new RoadMatrix(m, new double[3, 3], new bool[3, 3]);

        var plain = DaySplitter.Recalculate(days, Limit, null, matrix, spots);
        var anchored = DaySplitter.Recalculate(days, Limit, null, matrix, spots, 0, anchor);

        Assert.Equal(10, plain.Days[0].DriveMinutes);
        Assert.Equal(20, anchored.Days[0].DriveMinutes);
        Assert.Equal(new[] { "anchor>n2", "n2>n1", "n1>anchor" }, anchored.Days[0].Legs.Select(l => $"{l.FromId}>{l.ToId}").ToArray());
    }
}

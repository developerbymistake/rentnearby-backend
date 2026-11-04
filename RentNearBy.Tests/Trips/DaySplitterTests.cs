namespace RentNearBy.Tests.Trips;

public class DaySplitterTests
{
    private const int Limit = 450;
    private static readonly DateOnly Start = new(2026, 11, 2);

    private static TripSpot Spot(string id, int visit, double lat = 29.39, double lon = 79.45, int[]? closed = null)
        => new() { Id = id, Name = id, Latitude = lat, Longitude = lon, Category = "attraction", VisitMinutes = visit, ClosedDays = closed };

    // Symmetric road matrix, every pair `fallback` minutes unless set.
    private static RoadMatrix Matrix(int n, int fallback, params (int From, int To, int Minutes)[] cells)
    {
        var minutes = new int[n, n];
        var km = new double[n, n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            if (i != j) { minutes[i, j] = fallback; km[i, j] = 1.0; }
        foreach (var (from, to, m) in cells)
        {
            minutes[from, to] = m;
            minutes[to, from] = m;
        }
        return new RoadMatrix(minutes, km, new bool[n, n]);
    }

    private static int Iso(DateOnly date) => date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;

    private static SplitResult Split(IReadOnlyList<TripSpot> spots, RoadMatrix matrix, double centreLat = 29.39, double centreLon = 79.45,
        DateOnly? start = null, int maxDays = 14)
        => DaySplitter.Split(new SplitInput(spots, centreLat, centreLon, Limit, start, maxDays), matrix);

    private static string[] Ids(SplitDay day) => day.Spots.Select(s => s.Id).ToArray();

    [Fact]
    public void Split_OneSpot_OneDayNoLegs()
    {
        var spots = new[] { Spot("a", 90) };

        var result = Split(spots, DaySplitter.FillEstimates(spots, null, 30, 2));

        var day = Assert.Single(result.Days);
        Assert.Equal(new[] { "a" }, Ids(day));
        Assert.Empty(day.Legs);
        Assert.Equal(0, day.DriveMinutes);
        Assert.Equal(90, day.VisitMinutes);
        Assert.False(day.OverLimit);
    }

    [Fact]
    public void Split_SampleTable_CutsAfterEcoCaveGardens()
    {
        var spots = new[]
        {
            Spot("Mall Road", 90, 29.3919, 79.4542),
            Spot("Naina Devi Temple", 60, 29.3950, 79.4560),
            Spot("Naini Lake", 90, 29.3980, 79.4580),
            Spot("Eco Cave Gardens", 60, 29.4010, 79.4600),
            Spot("Snow View", 120, 29.4040, 79.4620),
        };
        var matrix = Matrix(5, 100, (0, 1, 8), (1, 2, 5), (2, 3, 12), (3, 4, 35));

        var result = Split(spots, matrix, 29.3919, 79.4542);

        Assert.Equal(2, result.Days.Count);
        Assert.Equal(new[] { "Mall Road", "Naina Devi Temple", "Naini Lake", "Eco Cave Gardens" }, Ids(result.Days[0]));
        Assert.Equal(300, result.Days[0].VisitMinutes);
        Assert.Equal(25, result.Days[0].DriveMinutes);
        Assert.Equal(325, result.Days[0].TotalMinutes);
        Assert.False(result.Days[0].OverLimit);
        Assert.Equal(new[] { "Snow View" }, Ids(result.Days[1]));
        Assert.Equal(2, result.Days[1].Day);
        Assert.Empty(result.Days[1].Legs);
    }

    [Fact]
    public void Split_StartsAtSpotNearestCentre()
    {
        var spots = new[] { Spot("far", 60, 29.50, 79.60), Spot("near", 60, 29.391, 79.451), Spot("mid", 60, 29.45, 79.50) };

        var result = Split(spots, Matrix(3, 10), 29.39, 79.45);

        Assert.Equal("near", result.Days[0].Spots[0].Id);
    }

    [Fact]
    public void Split_SpotLongerThanLimit_GetsOwnDayOverLimit()
    {
        var spots = new[] { Spot("a", 60, 29.39, 79.45), Spot("b", 500, 29.40, 79.46) };

        var result = Split(spots, Matrix(2, 10));

        Assert.Equal(2, result.Days.Count);
        Assert.Equal(new[] { "b" }, Ids(result.Days[1]));
        Assert.True(result.Days[1].OverLimit);
        Assert.Contains(result.Warnings, w => w.Code == TripWarningCodes.OverLimit && w.Day == 2);
    }

    [Fact]
    public void Split_WithoutRoadTimes_DayIsEstimate()
    {
        var spots = new[] { Spot("a", 60, 29.39, 79.45), Spot("b", 60, 29.40, 79.45) };
        var straight = GeoMath.HaversineKm(29.39, 79.45, 29.40, 79.45);

        var result = Split(spots, DaySplitter.FillEstimates(spots, null, 30, 2.0));

        var day = Assert.Single(result.Days);
        var leg = Assert.Single(day.Legs);
        Assert.True(leg.IsEstimate);
        Assert.Equal((int)Math.Ceiling(straight / 30 * 60 * 2.0), leg.Minutes);
        Assert.Equal(Math.Round(straight * 2.0, 1), leg.Km);
        Assert.True(day.IsEstimate);
        Assert.Contains(result.Warnings, w => w.Code == TripWarningCodes.Estimate && w.Day == 1);
    }

    [Fact]
    public void FillEstimates_KeepsRoadCells_FillsMissingOnes()
    {
        var spots = new[] { Spot("a", 60, 29.39, 79.45), Spot("b", 60, 29.40, 79.45) };
        var isEstimate = new bool[2, 2];
        isEstimate[1, 0] = true;
        var raw = new RoadMatrix(new[,] { { 0, 17 }, { 0, 0 } }, new[,] { { 0.0, 4.2 }, { 0.0, 0.0 } }, isEstimate);

        var filled = DaySplitter.FillEstimates(spots, raw, 30, 2.0);

        Assert.Equal(17, filled.Minutes![0, 1]);
        Assert.Equal(4.2, filled.Km![0, 1]);
        Assert.False(filled.IsEstimate[0, 1]);
        Assert.True(filled.IsEstimate[1, 0]);
        Assert.True(filled.Minutes![1, 0] > 0);
    }

    [Fact]
    public void Split_ClosedOnDayOne_MovesToNextDay()
    {
        var spots = new[] { Spot("a", 60, 29.39, 79.45), Spot("b", 60, 29.391, 79.451, [Iso(Start)]), Spot("c", 60, 29.40, 79.46) };
        var matrix = Matrix(3, 30, (0, 1, 5));

        var result = Split(spots, matrix, start: Start);

        Assert.Equal(2, result.Days.Count);
        Assert.Equal(new[] { "a", "c" }, Ids(result.Days[0]));
        Assert.Equal(new[] { "b" }, Ids(result.Days[1]));
        Assert.DoesNotContain(result.Warnings, w => w.Code == TripWarningCodes.ClosedDay);
    }

    [Fact]
    public void Split_ClosedOnEveryDayLeft_FoldsWithWarning()
    {
        var closed = new[] { Iso(Start), Iso(Start.AddDays(1)) };
        var spots = new[] { Spot("a", 60, 29.39, 79.45), Spot("b", 60, 29.40, 79.46, closed) };

        var result = Split(spots, Matrix(2, 20), start: Start, maxDays: 2);

        var day = Assert.Single(result.Days);
        Assert.Equal(new[] { "a", "b" }, Ids(day));
        var warning = Assert.Single(result.Warnings, w => w.Code == TripWarningCodes.ClosedDay);
        Assert.Equal("b", warning.SpotId);
        Assert.Equal(1, warning.Day);
        Assert.Equal("b is closed on that day", warning.Message);
    }

    [Fact]
    public void Split_ClosedOnNextDay_MovesToFirstOpenDay()
    {
        var spots = new[] { Spot("a", 400, 29.39, 79.45), Spot("b", 300, 29.40, 79.46, [Iso(Start.AddDays(1))]) };

        var result = Split(spots, Matrix(2, 20), start: Start);

        Assert.Equal(3, result.Days.Count);
        Assert.Equal(new[] { "a" }, Ids(result.Days[0]));
        Assert.Empty(result.Days[1].Spots);
        Assert.Equal(new[] { "b" }, Ids(result.Days[2]));
        Assert.Equal(3, result.Days[2].Day);
        Assert.All(result.Days, d => Assert.False(d.OverLimit));
        Assert.DoesNotContain(result.Warnings, w => w.Code == TripWarningCodes.ClosedDay);
    }

    [Fact]
    public void Split_AllClosedOnDayOne_StartsOnDayTwo()
    {
        var spots = new[] { Spot("a", 60, closed: [Iso(Start)]) };

        var result = Split(spots, DaySplitter.FillEstimates(spots, null, 30, 2), start: Start);

        Assert.Equal(2, result.Days.Count);
        Assert.Empty(result.Days[0].Spots);
        Assert.Equal(new[] { "a" }, Ids(result.Days[1]));
        Assert.DoesNotContain(result.Warnings, w => w.Code == TripWarningCodes.ClosedDay);
    }

    [Fact]
    public void Split_NoStartDate_ClosedDaysIgnored()
    {
        var spots = new[] { Spot("a", 60, 29.39, 79.45), Spot("b", 60, 29.391, 79.451, [1, 2, 3, 4, 5, 6, 7]) };

        var result = Split(spots, Matrix(2, 5));

        Assert.Equal(new[] { "a", "b" }, Ids(Assert.Single(result.Days)));
        Assert.DoesNotContain(result.Warnings, w => w.Code == TripWarningCodes.ClosedDay);
    }

    [Fact]
    public void Split_MoreDaysThanMax_RestGoesToLastDay()
    {
        var spots = new[] { Spot("a", 400, 29.39, 79.45), Spot("b", 400, 29.40, 79.46), Spot("c", 400, 29.41, 79.47) };
        var matrix = Matrix(3, 50, (0, 1, 10));

        var result = Split(spots, matrix, maxDays: 2);

        Assert.Equal(2, result.Days.Count);
        Assert.Equal(new[] { "a" }, Ids(result.Days[0]));
        Assert.Equal(new[] { "b", "c" }, Ids(result.Days[1]));
        Assert.True(result.Days[1].OverLimit);
        Assert.Contains(result.Warnings, w => w.Code == TripWarningCodes.TooManyDays && w.Day == 2);
    }

    [Fact]
    public void Recalculate_KeepsUserOrder()
    {
        var a = Spot("a", 60);
        var b = Spot("b", 60);
        var c = Spot("c", 60);
        var all = new[] { a, b, c };
        var matrix = Matrix(3, 40, (0, 1, 5), (1, 2, 7), (0, 2, 9));
        var days = new List<(int Day, List<TripSpot> Spots)> { (1, new List<TripSpot> { c, a, b }) };

        var result = DaySplitter.Recalculate(days, Limit, null, matrix, all);

        var day = Assert.Single(result.Days);
        Assert.Equal(new[] { "c", "a", "b" }, Ids(day));
        Assert.Equal(new[] { "c>a", "a>b" }, day.Legs.Select(l => $"{l.FromId}>{l.ToId}").ToArray());
        Assert.Equal(14, day.DriveMinutes);
        Assert.Equal(194, day.TotalMinutes);
    }

    [Fact]
    public void Recalculate_ClosedDay_WarnsWithoutMoving()
    {
        var a = Spot("a", 60, closed: [Iso(Start.AddDays(1))]);
        var b = Spot("b", 60);
        var days = new List<(int Day, List<TripSpot> Spots)> { (1, new List<TripSpot> { b }), (2, new List<TripSpot> { a }) };

        var result = DaySplitter.Recalculate(days, Limit, Start, Matrix(2, 10), new[] { a, b });

        Assert.Equal(new[] { "a" }, Ids(result.Days[1]));
        Assert.Contains(result.Warnings, w => w.Code == TripWarningCodes.ClosedDay && w.SpotId == "a" && w.Day == 2);
    }

    [Fact]
    public void Recalculate_DayOverLimit_Flagged()
    {
        var a = Spot("a", 300);
        var b = Spot("b", 200);
        var days = new List<(int Day, List<TripSpot> Spots)> { (1, new List<TripSpot> { a, b }) };

        var result = DaySplitter.Recalculate(days, Limit, null, Matrix(2, 10), new[] { a, b });

        Assert.True(Assert.Single(result.Days).OverLimit);
        Assert.Contains(result.Warnings, w => w.Code == TripWarningCodes.OverLimit && w.Day == 1);
    }

    [Fact]
    public void Haversine_OneDegreeLatitude_About111Km()
        => Assert.InRange(GeoMath.HaversineKm(29, 79, 30, 79), 111.0, 111.4);

    // D5: a forced day count never drops a place and never returns more days than asked.
    [Fact]
    public void Split_MaxDaysOne_PutsEveryPlaceInDayOneWithOverLimit()
    {
        var spots = Enumerable.Range(0, 6).Select(i => Spot($"s{i}", 120)).ToArray();

        var result = Split(spots, Matrix(6, 30), maxDays: 1);

        var day = Assert.Single(result.Days);
        Assert.Equal(6, day.Spots.Count);
        Assert.True(day.OverLimit);
        Assert.Contains(result.Warnings, w => w.Code == TripWarningCodes.OverLimit && w.Day == 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Split_MaxDays_NeverMoreDaysAndNoPlaceDropped(int maxDays)
    {
        var spots = Enumerable.Range(0, 10).Select(i => Spot($"s{i}", 150)).ToArray();

        var result = Split(spots, Matrix(10, 40), maxDays: maxDays);

        Assert.InRange(result.Days.Count, 1, maxDays);
        var placed = result.Days.SelectMany(d => d.Spots).Select(s => s.Id).ToList();
        Assert.Equal(10, placed.Count);
        Assert.Equal(10, placed.Distinct().Count());
        Assert.True(result.Days[^1].OverLimit);
    }

    [Fact]
    public void Split_MaxDaysWithClosedPlaces_KeepsEveryPlaceInsideTheLimit()
    {
        var monday = new DateOnly(2026, 11, 2);
        var spots = new[] { Spot("a", 60), Spot("b", 60, closed: [Iso(monday)]), Spot("c", 60, closed: [Iso(monday), Iso(monday.AddDays(1))]) };

        var result = Split(spots, Matrix(3, 20), start: monday, maxDays: 2);

        Assert.InRange(result.Days.Count, 1, 2);
        Assert.Equal(3, result.Days.SelectMany(d => d.Spots).Count());
    }

    [Fact]
    public void Split_MaxDaysAboveNeed_UsesFewerDays()
    {
        var spots = new[] { Spot("a", 60), Spot("b", 60) };

        var result = Split(spots, Matrix(2, 20), maxDays: 5);

        Assert.Single(result.Days);
    }
}

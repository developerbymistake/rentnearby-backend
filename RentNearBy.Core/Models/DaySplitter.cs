namespace RentNearBy.Core.Models;

public static class GeoMath
{
    private const double EarthRadiusKm = 6371.0;

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double deg) => deg * Math.PI / 180.0;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }
}

public static class TripWarningCodes
{
    public const string OverLimit = "OverLimit";
    public const string Estimate = "Estimate";
    public const string ClosedDay = "ClosedDay";
    public const string RouteMissing = "RouteMissing";
    public const string OsrmUnavailable = "OsrmUnavailable";
    public const string TooManyDays = "TooManyDays";
    public const string ClosedDaysCrowd = "ClosedDaysCrowd";
}

// One continuous path through every place (shortest total road time, start end nearest the centre or the anchor), cut into
// days by DaySplit (exact DP over the path). With an anchor (the stay) every day starts and ends there and those legs count.
public static class DaySplitter
{
    // A travel day with less free time than this takes no place.
    public const int MinFreeMinutes = 60;

    private sealed class DayBuilder(int day, int reserved = 0)
    {
        public int Day { get; } = day;
        public int Reserved { get; } = reserved;
        public List<int> Spots { get; } = new();
        public List<SplitLeg> Legs { get; } = new();
        public int Visit { get; set; }
        public int Drive { get; set; }
        public int Last => Spots.Count == 0 ? -1 : Spots[^1];
    }

    // anchor != null: matrix is (n + 1) x (n + 1) and its last row/column is the anchor.
    public static SplitResult Split(SplitInput input, RoadMatrix matrix, TripSpot? anchor = null)
    {
        var spots = input.Spots;
        var n = spots.Count;
        var maxDays = Math.Max(1, input.MaxDays);
        var warnings = new List<SplitWarning>();
        if (n == 0) return new SplitResult(new List<SplitDay>(), warnings);

        var minutes = matrix.Minutes!;
        var anchorIndex = anchor == null ? -1 : n;
        var order = anchor == null
            ? RoutePath.Order(minutes, spots, input.CentreLat, input.CentreLon)
            : RoutePath.Order(minutes, spots, anchor.Latitude, anchor.Longitude);
        var reserved = Math.Max(0, input.FirstDayUsedMinutes);
        var plan = DaySplit.Solve(new DaySplitInput(order, spots.Select(s => s.VisitMinutes).ToArray(), minutes, input.DayLimitMinutes, maxDays,
            reserved, anchorIndex, ClosedTable(spots, input.StartDate, maxDays)));

        var builders = new List<DayBuilder>(plan.Days.Length);
        for (var d = 0; d < plan.Days.Length; d++)
        {
            var builder = new DayBuilder(d + 1, d == 0 ? reserved : 0);
            foreach (var i in plan.Days[d])
            {
                Append(builder, i, spots, matrix, anchorIndex);
                AddClosedWarning(warnings, spots[i], builder.Day, input.StartDate);
            }
            CloseAtAnchor(builder, spots, matrix, anchorIndex);
            builders.Add(builder);
        }

        if (plan.Overflow > 0)
        {
            var worst = builders.MaxBy(b => b.Visit + b.Drive + b.Reserved)!.Day;
            warnings.Insert(0, plan.OpenOverflow == 0
                ? new SplitWarning(TripWarningCodes.ClosedDaysCrowd, $"Closed days make Day {worst} long", null, worst)
                : new SplitWarning(TripWarningCodes.TooManyDays, $"Places do not fit in {maxDays} days", null, worst));
        }

        return Finish(builders, spots, input.DayLimitMinutes, warnings);
    }

    // The user's order always wins: only times, flags and the closed-day warning are computed.
    // anchor != null: matrix is (n + 1) x (n + 1) over allSpots and its last row/column is the anchor.
    public static SplitResult Recalculate(IReadOnlyList<(int Day, List<TripSpot> Spots)> days, int dayLimitMinutes, DateOnly? startDate, RoadMatrix matrix, IReadOnlyList<TripSpot> allSpots,
        int firstDayUsedMinutes = 0, TripSpot? anchor = null)
    {
        var anchorIndex = anchor == null ? -1 : allSpots.Count;
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < allSpots.Count; i++)
            index.TryAdd(allSpots[i].Id, i);

        // Day numbers are local to the city leg; the app leaves out empty days, so the reserve goes on day 1 only.
        var reserved = Math.Max(0, firstDayUsedMinutes);
        var warnings = new List<SplitWarning>();
        var builders = new List<DayBuilder>();
        foreach (var (day, daySpots) in days)
        {
            var builder = new DayBuilder(day, day == 1 ? reserved : 0);
            foreach (var spot in daySpots)
            {
                if (!index.TryGetValue(spot.Id, out var i)) continue;
                Append(builder, i, allSpots, matrix, anchorIndex);
                AddClosedWarning(warnings, spot, day, startDate);
            }
            CloseAtAnchor(builder, allSpots, matrix, anchorIndex);
            builders.Add(builder);
        }

        return Finish(builders, allSpots, dayLimitMinutes, warnings);
    }

    public static RoadMatrix FillEstimates(IReadOnlyList<TripSpot> spots, RoadMatrix? matrix, double fallbackSpeedKmh, double estimateFactor)
    {
        var n = spots.Count;
        var speed = fallbackSpeedKmh > 0 ? fallbackSpeedKmh : 30;
        var factor = estimateFactor > 0 ? estimateFactor : 1;
        var minutes = new int[n, n];
        var km = new double[n, n];
        var isEstimate = new bool[n, n];

        var usable = matrix?.Minutes != null && matrix.Km != null
            && matrix.Minutes.GetLength(0) == n && matrix.Minutes.GetLength(1) == n
            && matrix.Km.GetLength(0) == n && matrix.Km.GetLength(1) == n
            && matrix.IsEstimate.GetLength(0) == n && matrix.IsEstimate.GetLength(1) == n;

        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
        {
            if (i == j) continue;
            if (usable && !matrix!.IsEstimate[i, j])
            {
                minutes[i, j] = matrix.Minutes![i, j];
                km[i, j] = matrix.Km![i, j];
                continue;
            }

            var straight = GeoMath.HaversineKm(spots[i].Latitude, spots[i].Longitude, spots[j].Latitude, spots[j].Longitude);
            minutes[i, j] = (int)Math.Ceiling(straight / speed * 60 * factor);
            km[i, j] = Math.Round(straight * factor, 1);
            isEstimate[i, j] = true;
        }

        return new RoadMatrix(minutes, km, isEstimate);
    }

    private static void Append(DayBuilder day, int spot, IReadOnlyList<TripSpot> spots, RoadMatrix matrix, int anchor)
    {
        var last = day.Last;
        if (last >= 0 || anchor >= 0)
            AddLeg(day, last >= 0 ? last : anchor, spot, spots, matrix, anchor);
        day.Spots.Add(spot);
        day.Visit += spots[spot].VisitMinutes;
    }

    private static void CloseAtAnchor(DayBuilder day, IReadOnlyList<TripSpot> spots, RoadMatrix matrix, int anchor)
    {
        if (anchor >= 0 && day.Last >= 0) AddLeg(day, day.Last, anchor, spots, matrix, anchor);
    }

    private static void AddLeg(DayBuilder day, int from, int to, IReadOnlyList<TripSpot> spots, RoadMatrix matrix, int anchor)
    {
        var m = matrix.Minutes![from, to];
        day.Legs.Add(new SplitLeg(IdOf(from, spots, anchor), IdOf(to, spots, anchor), m, matrix.Km![from, to], matrix.IsEstimate[from, to]));
        day.Drive += m;
    }

    private static string IdOf(int index, IReadOnlyList<TripSpot> spots, int anchor) => index == anchor ? TripAnchor.Id : spots[index].Id;

    // closed[place, day - 1]; null without a start date or when no place has closed days.
    private static bool[,]? ClosedTable(IReadOnlyList<TripSpot> spots, DateOnly? startDate, int maxDays)
    {
        if (startDate == null || spots.All(s => s.ClosedDays is not { Length: > 0 })) return null;
        var closed = new bool[spots.Count, maxDays];
        for (var d = 1; d <= maxDays; d++)
        {
            var weekday = WeekdayOf(startDate, d);
            for (var i = 0; i < spots.Count; i++)
                closed[i, d - 1] = IsClosed(spots[i], weekday);
        }
        return closed;
    }

    private static SplitResult Finish(List<DayBuilder> builders, IReadOnlyList<TripSpot> spots, int limit, List<SplitWarning> warnings)
    {
        var days = new List<SplitDay>(builders.Count);
        var dayWarnings = new List<SplitWarning>();
        foreach (var b in builders)
        {
            var km = Math.Round(b.Legs.Sum(l => l.Km), 1);
            var isEstimate = b.Legs.Any(l => l.IsEstimate);
            var overLimit = b.Visit + b.Drive + b.Reserved > limit;
            days.Add(new SplitDay(b.Day, b.Spots.Select(i => spots[i]).ToList(), b.Visit, b.Drive, km, isEstimate, overLimit, b.Legs));
            if (overLimit) dayWarnings.Add(new SplitWarning(TripWarningCodes.OverLimit, $"Day {b.Day} is over the limit", null, b.Day));
            if (isEstimate) dayWarnings.Add(new SplitWarning(TripWarningCodes.Estimate, $"Day {b.Day} uses estimated road times", null, b.Day));
        }
        dayWarnings.AddRange(warnings);
        return new SplitResult(days, dayWarnings);
    }

    // ISO weekday (Monday = 1 .. Sunday = 7) of trip day `day`, or null without a start date.
    private static int? WeekdayOf(DateOnly? startDate, int day)
    {
        if (startDate == null) return null;
        var dow = startDate.Value.AddDays(day - 1).DayOfWeek;
        return dow == DayOfWeek.Sunday ? 7 : (int)dow;
    }

    private static bool IsClosed(TripSpot spot, int? weekday)
        => weekday != null && spot.ClosedDays != null && spot.ClosedDays.Contains(weekday.Value);

    private static void AddClosedWarning(List<SplitWarning> warnings, TripSpot spot, int day, DateOnly? startDate)
    {
        if (IsClosed(spot, WeekdayOf(startDate, day)))
            warnings.Add(new SplitWarning(TripWarningCodes.ClosedDay, $"{spot.Name} is closed on that day", spot.Id, day));
    }
}

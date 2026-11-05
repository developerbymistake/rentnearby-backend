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
}

// Doc 4.1: nearest-neighbour by road minutes from the spot nearest the centre, cut into days at the limit.
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

    public static SplitResult Split(SplitInput input, RoadMatrix matrix)
    {
        var spots = input.Spots;
        var n = spots.Count;
        var limit = input.DayLimitMinutes;
        var maxDays = Math.Max(1, input.MaxDays);
        var warnings = new List<SplitWarning>();
        if (n == 0) return new SplitResult(new List<SplitDay>(), warnings);

        var minutes = matrix.Minutes!;
        var builders = new List<DayBuilder>();
        var visited = new bool[n];
        var remaining = n;
        var current = new DayBuilder(1, Math.Max(0, input.FirstDayUsedMinutes));
        builders.Add(current);
        var pending = -1;

        while (remaining > 0)
        {
            var weekday = WeekdayOf(input.StartDate, current.Day);

            if (current.Spots.Count == 0)
            {
                if (current.Reserved > 0 && current.Day < maxDays && limit - current.Reserved < MinFreeMinutes)
                {
                    current = new DayBuilder(current.Day + 1);
                    builders.Add(current);
                    continue;
                }

                int start;
                if (pending >= 0 && !IsClosed(spots[pending], weekday))
                {
                    start = pending;
                }
                else
                {
                    var from = LastPlaced(builders);
                    start = from < 0
                        ? NearestToCentre(spots, visited, weekday, input.CentreLat, input.CentreLon)
                        : NearestByRoad(minutes, visited, spots, weekday, from);
                }

                if (start >= 0)
                {
                    if (current.Reserved > 0 && current.Day < maxDays && current.Reserved + spots[start].VisitMinutes > limit)
                    {
                        current = new DayBuilder(current.Day + 1);
                        builders.Add(current);
                        pending = start;
                        continue;
                    }
                    pending = -1;
                    Append(current, start, spots, matrix);
                    visited[start] = true;
                    remaining--;
                    continue;
                }

                // Every place left is closed today: leave the day free if one of them opens on a later day.
                if (current.Day < maxDays && OpensLater(spots, visited, input.StartDate, current.Day, maxDays))
                {
                    current = new DayBuilder(current.Day + 1);
                    builders.Add(current);
                    continue;
                }

                if (current.Day > 1)
                {
                    builders.RemoveAt(builders.Count - 1);
                    current = builders[^1];
                }
                AppendClosedRest(current, visited, spots, matrix, warnings, input.StartDate, input.CentreLat, input.CentreLon);
                break;
            }

            var last = current.Last;
            var candidate = NearestByRoad(minutes, visited, spots, weekday, last);
            if (candidate < 0)
            {
                // Places left are closed today; they move to a later day.
                if (current.Day >= maxDays)
                {
                    AppendTooManyDays(current, visited, spots, matrix, warnings, input.StartDate);
                    break;
                }
                current = new DayBuilder(current.Day + 1);
                builders.Add(current);
                continue;
            }

            var candidateTotal = current.Visit + current.Drive + current.Reserved + minutes[last, candidate] + spots[candidate].VisitMinutes;
            if (candidateTotal > limit)
            {
                if (current.Day >= maxDays)
                {
                    AppendTooManyDays(current, visited, spots, matrix, warnings, input.StartDate);
                    break;
                }
                current = new DayBuilder(current.Day + 1);
                builders.Add(current);
                pending = candidate;
                continue;
            }

            Append(current, candidate, spots, matrix);
            visited[candidate] = true;
            remaining--;
        }

        return Finish(builders, spots, limit, warnings);
    }

    // The user's order always wins: only times, flags and the closed-day warning are computed.
    public static SplitResult Recalculate(IReadOnlyList<(int Day, List<TripSpot> Spots)> days, int dayLimitMinutes, DateOnly? startDate, RoadMatrix matrix, IReadOnlyList<TripSpot> allSpots,
        int firstDayUsedMinutes = 0)
    {
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
                Append(builder, i, allSpots, matrix);
                AddClosedWarning(warnings, spot, day, startDate);
            }
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

    private static void Append(DayBuilder day, int spot, IReadOnlyList<TripSpot> spots, RoadMatrix matrix)
    {
        var last = day.Last;
        if (last >= 0)
        {
            var m = matrix.Minutes![last, spot];
            day.Legs.Add(new SplitLeg(spots[last].Id, spots[spot].Id, m, matrix.Km![last, spot], matrix.IsEstimate[last, spot]));
            day.Drive += m;
        }
        day.Spots.Add(spot);
        day.Visit += spots[spot].VisitMinutes;
    }

    private static void AppendRest(DayBuilder day, bool[] visited, IReadOnlyList<TripSpot> spots, RoadMatrix matrix, List<SplitWarning> warnings, DateOnly? startDate)
    {
        while (true)
        {
            var next = NearestByRoad(matrix.Minutes!, visited, spots, null, day.Last);
            if (next < 0) return;
            Append(day, next, spots, matrix);
            visited[next] = true;
            AddClosedWarning(warnings, spots[next], day.Day, startDate);
        }
    }

    // No later open day within the limit: the rest joins this day and each place is flagged.
    private static void AppendClosedRest(DayBuilder day, bool[] visited, IReadOnlyList<TripSpot> spots, RoadMatrix matrix, List<SplitWarning> warnings,
        DateOnly? startDate, double centreLat, double centreLon)
    {
        var weekday = WeekdayOf(startDate, day.Day);
        var next = day.Last < 0
            ? NearestToCentre(spots, visited, null, centreLat, centreLon)
            : NearestByRoad(matrix.Minutes!, visited, spots, null, day.Last);
        while (next >= 0)
        {
            Append(day, next, spots, matrix);
            visited[next] = true;
            var spot = spots[next];
            var message = IsClosed(spot, weekday) ? $"{spot.Name} is closed on that day" : $"{spot.Name} is closed on the later days";
            warnings.Add(new SplitWarning(TripWarningCodes.ClosedDay, message, spot.Id, day.Day));
            next = NearestByRoad(matrix.Minutes!, visited, spots, null, day.Last);
        }
    }

    private static int LastPlaced(List<DayBuilder> builders)
    {
        for (var i = builders.Count - 1; i >= 0; i--)
            if (builders[i].Last >= 0) return builders[i].Last;
        return -1;
    }

    private static bool OpensLater(IReadOnlyList<TripSpot> spots, bool[] visited, DateOnly? startDate, int day, int maxDays)
    {
        if (startDate == null) return false;
        var lastDay = Math.Min(maxDays, day + 7);
        for (var d = day + 1; d <= lastDay; d++)
        {
            var weekday = WeekdayOf(startDate, d);
            for (var i = 0; i < spots.Count; i++)
                if (!visited[i] && !IsClosed(spots[i], weekday)) return true;
        }
        return false;
    }

    private static void AppendTooManyDays(DayBuilder day, bool[] visited, IReadOnlyList<TripSpot> spots, RoadMatrix matrix, List<SplitWarning> warnings, DateOnly? startDate)
    {
        warnings.Add(new SplitWarning(TripWarningCodes.TooManyDays, $"Places beyond {day.Day} days were added to Day {day.Day}", null, day.Day));
        AppendRest(day, visited, spots, matrix, warnings, startDate);
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

    private static int NearestToCentre(IReadOnlyList<TripSpot> spots, bool[] visited, int? weekday, double lat, double lon)
    {
        var best = -1;
        var bestKm = double.MaxValue;
        for (var i = 0; i < spots.Count; i++)
        {
            if (visited[i] || IsClosed(spots[i], weekday)) continue;
            var km = GeoMath.HaversineKm(lat, lon, spots[i].Latitude, spots[i].Longitude);
            if (km < bestKm) { bestKm = km; best = i; }
        }
        return best;
    }

    // Ties go to the smaller index; from = -1 falls back to the first open spot.
    private static int NearestByRoad(int[,] minutes, bool[] visited, IReadOnlyList<TripSpot> spots, int? weekday, int from)
    {
        var best = -1;
        var bestMinutes = int.MaxValue;
        for (var i = 0; i < spots.Count; i++)
        {
            if (visited[i] || IsClosed(spots[i], weekday)) continue;
            var m = from >= 0 ? minutes[from, i] : 0;
            if (m < bestMinutes) { bestMinutes = m; best = i; }
        }
        return best;
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

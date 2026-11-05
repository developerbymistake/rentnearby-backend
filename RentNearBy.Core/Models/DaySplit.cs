namespace RentNearBy.Core.Models;

// The day base (the stay) of a plan request: one extra matrix row/column after the places.
public static class TripAnchor
{
    public const string Id = "anchor";

    // Both or neither; finite, in range and within maxKm of the city centre.
    public static bool IsValid(double? lat, double? lon, double centreLat, double centreLon, double maxKm)
    {
        if (lat == null && lon == null) return true;
        if (lat is not { } a || lon is not { } b) return false;
        if (!double.IsFinite(a) || !double.IsFinite(b) || a < -90 || a > 90 || b < -180 || b > 180) return false;
        return GeoMath.HaversineKm(centreLat, centreLon, a, b) <= maxKm;
    }

    public static TripSpot? Spot(double? lat, double? lon)
        => lat is { } a && lon is { } b ? new TripSpot { Id = Id, Name = Id, Latitude = a, Longitude = b } : null;

    // Puts an n0 x n0 matrix into the top-left of an n x n one; the other cells are estimates.
    public static RoadMatrix? Embed(RoadMatrix? inner, int n)
    {
        if (inner?.Minutes == null || inner.Km == null) return null;
        var n0 = inner.Minutes.GetLength(0);
        if (n0 > n) return null;
        var minutes = new int[n, n];
        var km = new double[n, n];
        var isEstimate = new bool[n, n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
        {
            if (i == j) continue;
            if (i < n0 && j < n0)
            {
                minutes[i, j] = inner.Minutes[i, j];
                km[i, j] = inner.Km[i, j];
                isEstimate[i, j] = inner.IsEstimate[i, j];
            }
            else
            {
                isEstimate[i, j] = true;
            }
        }
        return new RoadMatrix(minutes, km, isEstimate);
    }
}

// Tour = place indices in RoutePath order. Visit and Minutes are indexed by place (Minutes may carry the anchor row).
// Closed[place, day - 1] = closed on that trip day; null = no start date.
public sealed record DaySplitInput(int[] Tour, int[] Visit, int[,] Minutes, int Limit, int MaxDays, int Reserved = 0, int Anchor = -1, bool[,]? Closed = null);

// Days[d - 1] = places of day d in visiting order (an empty day is a free day). Overflow = minutes the fullest
// multi-place day runs over the limit, above 0 only when MaxDays is too few. OpenOverflow = the same for the input tour
// with no closed days, so Overflow > 0 with OpenOverflow 0 means closed days, not the day count, crowd a day.
public sealed record DaySplitResult(int[] Tour, List<int>[] Days, int Violations, long Overflow, long Drive, long SlackSquares, long Weighted)
{
    public long OpenOverflow { get; init; }
    public int Evaluations { get; init; }
}

// Route-first, cluster-second (Prins 2004): an exact DP cuts the tour into days.
// A day is a stretch i..j-1 of the tour: anchor to first + visits + inner legs + last to anchor, plus Reserved on day 1.
// Pass 1 finds the fewest closed-day violations, then the least overflow a day must carry (0 when the days suffice).
// Pass 2, with every day capped at limit + that overflow, minimises (violations, days, drive, squared slack, day-weighted
// minutes): cuts fall between areas, days come out balanced and earlier days fuller on a tie. Pass 1 is O(D * n^2), pass 2
// O(D * n * k) with k = most places a day holds; one evaluation of n = 60, D = 14 is about 30k steps on reused buffers.
// Closed-day repair: the same tour split with no closed days is the bound. While the split is worse than that bound, only
// the places that sit on one of their closed days (in the split or in the bound) are moved alone (Or-opt), and only to the
// O(D) positions that can change a day: every day boundary, the cheapest insertion inside each day the place is open,
// and both tour ends. The whole tour is also tried reversed (a closed day makes the direction matter). At most
// MaxEvaluations splits per Solve.
public static class DaySplit
{
    private const int MaxMovePasses = 4;
    public const int MaxEvaluations = 300;

    public static DaySplitResult Solve(DaySplitInput input)
    {
        var search = new Search(input);
        var best = search.Evaluate(input.Tour, input.Closed);
        if (input.Closed == null || input.Tour.Length < 2)
            return best with { OpenOverflow = best.Overflow, Evaluations = search.Count };

        var bound = search.Evaluate(input.Tour, null);
        var openOverflow = bound.Overflow;

        for (var pass = 0; pass < MaxMovePasses && Compare(best, bound) > 0; pass++)
        {
            var improved = false;
            foreach (var place in Conflicting(input.Closed, best, bound))
            {
                var tour = best.Tour;
                var from = Array.IndexOf(tour, place);
                DaySplitResult? candidate = null;
                foreach (var to in Targets(input, best, place))
                {
                    if (search.Exhausted) break;
                    var result = search.Evaluate(Move(tour, from, to), input.Closed);
                    if (Compare(result, candidate ?? best) < 0) candidate = result;
                }
                if (candidate == null) continue;
                best = candidate;
                improved = true;
            }
            if (search.Exhausted) break;

            var flipped = search.Evaluate(Enumerable.Reverse(best.Tour).ToArray(), input.Closed);
            if (Compare(flipped, best) < 0)
            {
                best = flipped;
                improved = true;
            }
            if (!improved || search.Exhausted) break;
            bound = search.Evaluate(best.Tour, null);
        }
        return best with { OpenOverflow = openOverflow, Evaluations = search.Count };
    }

    public static int Compare(DaySplitResult a, DaySplitResult b)
    {
        int c;
        if ((c = a.Violations.CompareTo(b.Violations)) != 0) return c;
        if ((c = a.Overflow.CompareTo(b.Overflow)) != 0) return c;
        if ((c = a.Days.Length.CompareTo(b.Days.Length)) != 0) return c;
        if ((c = a.Drive.CompareTo(b.Drive)) != 0) return c;
        if ((c = a.SlackSquares.CompareTo(b.SlackSquares)) != 0) return c;
        return a.Weighted.CompareTo(b.Weighted);
    }

    // Tour order; a place counts when it sits on one of its closed days in the split or in the bound.
    private static List<int> Conflicting(bool[,] closed, DaySplitResult best, DaySplitResult bound)
    {
        var dayOf = new Dictionary<int, (int Best, int Bound)>();
        for (var d = 0; d < best.Days.Length; d++)
            foreach (var p in best.Days[d]) dayOf[p] = (d, -1);
        for (var d = 0; d < bound.Days.Length; d++)
            foreach (var p in bound.Days[d])
                if (dayOf.TryGetValue(p, out var at)) dayOf[p] = (at.Best, d);

        var list = new List<int>();
        foreach (var p in best.Tour)
            if (dayOf.TryGetValue(p, out var at) && (IsClosed(closed, p, at.Best) || IsClosed(closed, p, at.Bound)))
                list.Add(p);
        return list;
    }

    private static bool IsClosed(bool[,]? closed, int place, int day)
        => closed != null && day >= 0 && day < closed.GetLength(1) && closed[place, day];

    // Insert positions in the tour without the place, ascending: both ends, every day boundary, and the cheapest
    // insertion inside each day the place is open.
    private static List<int> Targets(DaySplitInput input, DaySplitResult best, int place)
    {
        var tour = best.Tour;
        var n = tour.Length;
        var from = Array.IndexOf(tour, place);
        var m = input.Minutes;
        int Rest(int x) => x < from ? tour[x] : tour[x + 1];

        var targets = new SortedSet<int> { 0, n - 1 };
        var start = 0;
        for (var k = 0; k < best.Days.Length; k++)
        {
            var count = best.Days[k].Count - (best.Days[k].Contains(place) ? 1 : 0);
            if (count == 0) continue;
            var end = start + count;
            targets.Add(start);
            targets.Add(end);
            if (!IsClosed(input.Closed, place, k))
            {
                var at = -1;
                var cheapest = long.MaxValue;
                for (var pos = start + 1; pos < end; pos++)
                {
                    var a = Rest(pos - 1);
                    var b = Rest(pos);
                    var delta = (long)m[a, place] + m[place, b] - m[a, b];
                    if (delta >= cheapest) continue;
                    cheapest = delta;
                    at = pos;
                }
                if (at >= 0) targets.Add(at);
            }
            start = end;
        }
        targets.Remove(from);
        return targets.ToList();
    }

    private static int[] Move(int[] tour, int from, int to)
    {
        var rest = new List<int>(tour);
        var place = rest[from];
        rest.RemoveAt(from);
        rest.Insert(to, place);
        return rest.ToArray();
    }

    // One set of DP buffers per Solve: every tour of a Solve has the same length.
    private sealed class Search
    {
        private readonly DaySplitInput _input;
        private readonly int _maxDays;
        private readonly long[] _visitSum;
        private readonly long[] _innerSum;
        private readonly long[] _toFirst;
        private readonly long[] _fromLast;
        private readonly int[,]? _closedSum;
        private readonly int[,] _v1;
        private readonly long[,] _o1;
        private readonly int[,] _v2;
        private readonly long[,] _drive;
        private readonly long[,] _squares;
        private readonly long[,] _weighted;
        private readonly int[,] _parent;

        public int Count { get; private set; }
        public bool Exhausted => Count >= MaxEvaluations;

        public Search(DaySplitInput input)
        {
            _input = input;
            _maxDays = Math.Max(1, input.MaxDays);
            var n = input.Tour.Length;
            _visitSum = new long[n + 1];
            _innerSum = new long[n + 1];
            _toFirst = new long[n];
            _fromLast = new long[n];
            if (input.Closed != null) _closedSum = new int[_maxDays, n + 1];
            _v1 = new int[_maxDays + 1, n + 1];
            _o1 = new long[_maxDays + 1, n + 1];
            _v2 = new int[_maxDays + 1, n + 1];
            _drive = new long[_maxDays + 1, n + 1];
            _squares = new long[_maxDays + 1, n + 1];
            _weighted = new long[_maxDays + 1, n + 1];
            _parent = new int[_maxDays + 1, n + 1];
        }

        public DaySplitResult Evaluate(int[] tour, bool[,]? closed)
        {
            Count++;
            var n = tour.Length;
            if (n == 0) return new DaySplitResult(tour, [], 0, 0, 0, 0, 0);

            var input = _input;
            var maxDays = _maxDays;
            long limit = input.Limit;
            long reserved = Math.Max(0, input.Reserved);
            var m = input.Minutes;
            var anchor = input.Anchor;
            var visitSum = _visitSum;
            var innerSum = _innerSum;
            var toFirst = _toFirst;
            var fromLast = _fromLast;

            for (var x = 0; x < n; x++)
            {
                visitSum[x + 1] = visitSum[x] + Math.Max(0, input.Visit[tour[x]]);
                innerSum[x + 1] = innerSum[x] + (x > 0 ? m[tour[x - 1], tour[x]] : 0);
                toFirst[x] = anchor >= 0 ? m[anchor, tour[x]] : 0;
                fromLast[x] = anchor >= 0 ? m[tour[x], anchor] : 0;
            }

            int[,]? closedSum = null;
            if (closed != null && _closedSum != null)
            {
                closedSum = _closedSum;
                var days = Math.Min(maxDays, closed.GetLength(1));
                for (var d = 0; d < days; d++)
                for (var x = 0; x < n; x++)
                    closedSum[d, x + 1] = closedSum[d, x] + (closed[tour[x], d] ? 1 : 0);
            }

            long Reserve(int d) => d == 1 ? reserved : 0;
            // A single place on a day without a reserve always fits: it cannot be split further.
            bool Exempt(int i, int j, int d) => j - i == 1 && !(d == 1 && reserved > 0);
            int Violations(int i, int j, int d) => closedSum == null ? 0 : closedSum[d - 1, j] - closedSum[d - 1, i];
            long Drive(int i, int j) => innerSum[j] - innerSum[i + 1] + toFirst[i] + fromLast[j - 1];
            long Load(int i, int j, int d) => visitSum[j] - visitSum[i] + Drive(i, j) + Reserve(d);

            // Pass 1: (violations, largest overflow).
            var v1 = _v1;
            var o1 = _o1;
            (int Violations, long Overflow) Pass1(bool blocked)
            {
                for (var d = 0; d <= maxDays; d++)
                for (var j = 0; j <= n; j++)
                    v1[d, j] = int.MaxValue;
                v1[0, 0] = 0;
                o1[0, 0] = 0;
                for (var d = 1; d <= maxDays; d++)
                for (var j = 0; j <= n; j++)
                for (var i = 0; i <= j; i++)
                {
                    if (v1[d - 1, i] == int.MaxValue) continue;
                    if (i < j && d == 1 && blocked) continue;
                    var viol = v1[d - 1, i];
                    var over = o1[d - 1, i];
                    if (i < j)
                    {
                        viol += Violations(i, j, d);
                        if (!Exempt(i, j, d)) over = Math.Max(over, Load(i, j, d) - limit);
                    }
                    if (viol < v1[d, j] || (viol == v1[d, j] && over < o1[d, j]))
                    {
                        v1[d, j] = viol;
                        o1[d, j] = over;
                    }
                }

                var least = long.MaxValue;
                var bestViolations = int.MaxValue;
                for (var d = 1; d <= maxDays; d++)
                {
                    if (v1[d, n] < bestViolations || (v1[d, n] == bestViolations && o1[d, n] < least))
                    {
                        bestViolations = v1[d, n];
                        least = o1[d, n];
                    }
                }
                return (bestViolations, Math.Max(0, least));
            }

            // A travel day with too little free time takes no place, unless it is the only day or keeping it free
            // makes the fullest day run longer than placing visits on it.
            var dayOneBlocked = maxDays > 1 && reserved > 0 && limit - reserved < DaySplitter.MinFreeMinutes;
            var first = Pass1(dayOneBlocked);
            if (dayOneBlocked && first.Overflow > 0)
            {
                var open = Pass1(false);
                if (open.Violations < first.Violations || (open.Violations == first.Violations && open.Overflow < first.Overflow))
                {
                    dayOneBlocked = false;
                    first = open;
                }
            }
            var overflow = first.Overflow;
            var cap = limit + overflow;

            // Pass 2: (violations, drive, squared slack, day-weighted minutes) with every day inside the cap.
            var v2 = _v2;
            var drive = _drive;
            var squares = _squares;
            var weighted = _weighted;
            var parent = _parent;
            for (var d = 0; d <= maxDays; d++)
            for (var j = 0; j <= n; j++)
                v2[d, j] = int.MaxValue;
            v2[0, 0] = 0;
            drive[0, 0] = 0;
            squares[0, 0] = 0;
            weighted[0, 0] = 0;

            bool Better(int d, int j, int viol, long dr, long sq, long wt)
            {
                if (viol != v2[d, j]) return viol < v2[d, j];
                if (dr != drive[d, j]) return dr < drive[d, j];
                if (sq != squares[d, j]) return sq < squares[d, j];
                return wt < weighted[d, j];
            }

            for (var d = 1; d <= maxDays; d++)
            for (var j = 0; j <= n; j++)
            {
                if (v2[d - 1, j] != int.MaxValue)
                {
                    var slack = limit - Reserve(d);
                    var sq = squares[d - 1, j] + slack * slack;
                    if (Better(d, j, v2[d - 1, j], drive[d - 1, j], sq, weighted[d - 1, j]))
                    {
                        v2[d, j] = v2[d - 1, j];
                        drive[d, j] = drive[d - 1, j];
                        squares[d, j] = sq;
                        weighted[d, j] = weighted[d - 1, j];
                        parent[d, j] = j;
                    }
                }

                if (d == 1 && dayOneBlocked) continue;
                for (var i = j - 1; i >= 0; i--)
                {
                    // Visits and inner legs only grow as the day starts earlier on the tour.
                    var core = visitSum[j] - visitSum[i] + innerSum[j] - innerSum[i + 1] + Reserve(d);
                    var exempt = Exempt(i, j, d);
                    if (!exempt && core > cap) break;
                    if (v2[d - 1, i] == int.MaxValue) continue;

                    var load = Load(i, j, d);
                    if (!exempt && load > cap) continue;
                    var dayDrive = Drive(i, j);
                    var slack = limit - load;
                    var viol = v2[d - 1, i] + Violations(i, j, d);
                    var dr = drive[d - 1, i] + dayDrive;
                    var sq = squares[d - 1, i] + slack * slack;
                    var wt = weighted[d - 1, i] + d * (visitSum[j] - visitSum[i] + dayDrive);
                    if (!Better(d, j, viol, dr, sq, wt)) continue;
                    v2[d, j] = viol;
                    drive[d, j] = dr;
                    squares[d, j] = sq;
                    weighted[d, j] = wt;
                    parent[d, j] = i;
                }
            }

            var used = 0;
            for (var d = 1; d <= maxDays; d++)
                if (v2[d, n] != int.MaxValue && (used == 0 || v2[d, n] < v2[used, n])) used = d;

            var result = new List<int>[used];
            for (int d = used, j = n; d >= 1; d--)
            {
                var i = parent[d, j];
                result[d - 1] = tour[i..j].ToList();
                j = i;
            }
            return new DaySplitResult(tour, result, v2[used, n], overflow, drive[used, n], squares[used, n], weighted[used, n]);
        }
    }
}

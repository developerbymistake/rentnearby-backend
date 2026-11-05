namespace RentNearBy.Core.Models;

// Orders the chosen places as ONE continuous path with the least total road time, so a trip never goes to an area,
// comes back and goes again. Deterministic: same input, same order. Road times are symmetrised (a to b plus b to a).
public static class RoutePath
{
    private const int MaxPasses = 50;

    // Returns indices into `spots`. The end of the path that is nearer the city centre comes first.
    public static int[] Order(int[,] minutes, IReadOnlyList<TripSpot> spots, double centreLat, double centreLon)
    {
        var n = spots.Count;
        if (n <= 2)
        {
            var small = Enumerable.Range(0, n).ToArray();
            return OrientToCentre(small, spots, centreLat, centreLon);
        }

        var cost = new long[n, n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            cost[i, j] = i == j ? 0 : (long)minutes[i, j] + minutes[j, i];

        // Shortest total wins; on a tie the path whose first place is nearest the centre.
        long bestTotal = long.MaxValue;
        var bestFirstKm = double.MaxValue;
        int[]? best = null;
        for (var s = 0; s < n; s++)
        {
            var path = NearestFrom(cost, n, s);
            Improve(path, cost);
            path = OrientToCentre(path, spots, centreLat, centreLon);
            var total = Total(path, cost);
            var firstKm = GeoMath.HaversineKm(centreLat, centreLon, spots[path[0]].Latitude, spots[path[0]].Longitude);
            if (total < bestTotal || (total == bestTotal && firstKm < bestFirstKm))
            {
                bestTotal = total;
                bestFirstKm = firstKm;
                best = path;
            }
        }

        return best!;
    }

    private static int[] NearestFrom(long[,] cost, int n, int start)
    {
        var path = new int[n];
        var used = new bool[n];
        path[0] = start;
        used[start] = true;
        for (var k = 1; k < n; k++)
        {
            var from = path[k - 1];
            var next = -1;
            var nextCost = long.MaxValue;
            for (var j = 0; j < n; j++)
            {
                if (used[j]) continue;
                if (cost[from, j] < nextCost) { nextCost = cost[from, j]; next = j; }
            }
            path[k] = next;
            used[next] = true;
        }
        return path;
    }

    private static long Total(int[] path, long[,] cost)
    {
        long total = 0;
        for (var k = 0; k + 1 < path.Length; k++) total += cost[path[k], path[k + 1]];
        return total;
    }

    // 2-opt (reverse a stretch) and Or-opt (move a stretch of 1 to 3 places) until nothing shortens the path.
    private static void Improve(int[] path, long[,] cost)
    {
        var n = path.Length;
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var improved = false;

            for (var i = 0; i < n - 1; i++)
            for (var j = i + 1; j < n; j++)
            {
                long before = 0, after = 0;
                if (i > 0) { before += cost[path[i - 1], path[i]]; after += cost[path[i - 1], path[j]]; }
                if (j < n - 1) { before += cost[path[j], path[j + 1]]; after += cost[path[i], path[j + 1]]; }
                if (after < before)
                {
                    Array.Reverse(path, i, j - i + 1);
                    improved = true;
                }
            }

            for (var len = 1; len <= 3 && len < n; len++)
            for (var i = 0; i + len <= n; i++)
            {
                if (TryMove(path, cost, i, len)) improved = true;
            }

            if (!improved) return;
        }
    }

    // Moves path[i .. i+len-1] (kept in order, or reversed) to the best other gap when that shortens the path.
    private static bool TryMove(int[] path, long[,] cost, int i, int len)
    {
        var n = path.Length;
        var j = i + len - 1;
        var first = path[i];
        var last = path[j];
        var prev = i > 0 ? path[i - 1] : -1;
        var next = j < n - 1 ? path[j + 1] : -1;

        long removeGain = 0;
        if (prev >= 0) removeGain += cost[prev, first];
        if (next >= 0) removeGain += cost[last, next];
        if (prev >= 0 && next >= 0) removeGain -= cost[prev, next];

        var rest = new List<int>(n - len);
        for (var k = 0; k < n; k++) if (k < i || k > j) rest.Add(path[k]);

        long bestDelta = 0;
        var bestGap = -1;
        var bestReverse = false;
        for (var gap = 0; gap <= rest.Count; gap++)
        {
            if (gap == i) continue;
            var a = gap > 0 ? rest[gap - 1] : -1;
            var b = gap < rest.Count ? rest[gap] : -1;
            foreach (var reverse in new[] { false, true })
            {
                var head = reverse ? last : first;
                var tail = reverse ? first : last;
                long add = 0;
                if (a >= 0) add += cost[a, head];
                if (b >= 0) add += cost[tail, b];
                if (a >= 0 && b >= 0) add -= cost[a, b];
                var delta = add - removeGain;
                if (delta < bestDelta) { bestDelta = delta; bestGap = gap; bestReverse = reverse; }
            }
        }
        if (bestGap < 0) return false;

        var segment = new int[len];
        Array.Copy(path, i, segment, 0, len);
        if (bestReverse) Array.Reverse(segment);
        rest.InsertRange(bestGap, segment);
        for (var k = 0; k < n; k++) path[k] = rest[k];
        return true;
    }

    private static int[] OrientToCentre(int[] path, IReadOnlyList<TripSpot> spots, double lat, double lon)
    {
        if (path.Length < 2) return path;
        var first = GeoMath.HaversineKm(lat, lon, spots[path[0]].Latitude, spots[path[0]].Longitude);
        var last = GeoMath.HaversineKm(lat, lon, spots[path[^1]].Latitude, spots[path[^1]].Longitude);
        if (last < first) Array.Reverse(path);
        return path;
    }
}

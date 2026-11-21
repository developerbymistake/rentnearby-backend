using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// Whole-rupee (and whole-guest) apportionment: every split sums exactly to its total, no per-line rounding drift.
public static class MoneySplit
{
    // Offer lines: at most 5 lines x 10 units (TripExecutionRules.MaxLinesPerOffer, offer validator).
    public const int MaxUnits = 50;

    // Largest remainder (Hamilton): floor of each exact share, the leftover units go to the largest fractions,
    // ties to the lower index. All weights zero = an even split. O(n log n).
    public static int[] Apportion(int total, IReadOnlyList<long> weights)
    {
        var n = weights.Count;
        var shares = new int[n];
        if (n == 0 || total <= 0) return shares;

        var w = weights.Select(x => Math.Max(0L, x)).ToArray();
        long sum = w.Sum();
        if (sum == 0)
        {
            Array.Fill(w, 1L);
            sum = n;
        }

        var rest = new (long Remainder, int Index)[n];
        long given = 0;
        for (var i = 0; i < n; i++)
        {
            var exact = (Int128)total * w[i];
            shares[i] = (int)(exact / sum);
            rest[i] = ((long)(exact % sum), i);
            given += shares[i];
        }

        var left = total - given;
        foreach (var r in rest.OrderByDescending(r => r.Remainder).ThenBy(r => r.Index))
        {
            if (left <= 0) break;
            shares[r.Index]++;
            left--;
        }
        return shares;
    }

    // Hamilton under per-part caps: a part that would pass its cap is fixed at it and the rest is split again.
    // When total exceeds the caps' sum the overflow is spread by weight with no cap. O(n^2 log n).
    public static int[] Apportion(int total, IReadOnlyList<long> weights, IReadOnlyList<int> caps)
    {
        var n = weights.Count;
        var shares = new int[n];
        if (n == 0 || total <= 0) return shares;

        long capSum = caps.Sum(c => (long)Math.Max(0, c));
        if (total >= capSum)
        {
            for (var i = 0; i < n; i++) shares[i] = Math.Max(0, caps[i]);
            var extra = Apportion((int)(total - capSum), weights);
            for (var i = 0; i < n; i++) shares[i] += extra[i];
            return shares;
        }

        var open = Enumerable.Range(0, n).Where(i => caps[i] > 0).ToList();
        var left = total;
        while (open.Count > 0 && left > 0)
        {
            var part = Apportion(left, open.Select(i => weights[i]).ToList());
            var over = open.Where((i, k) => part[k] > caps[i]).ToList();
            if (over.Count == 0)
            {
                for (var k = 0; k < open.Count; k++) shares[open[k]] = part[k];
                return shares;
            }
            foreach (var i in over)
            {
                shares[i] = caps[i];
                left -= caps[i];
            }
            open = open.Except(over).ToList();
        }
        return shares;
    }

    // amount x part / whole rounded half up in exact integers (for part <= whole: the 2-part Hamilton share of `part`).
    public static int ProRata(int amount, long part, long whole)
    {
        if (whole <= 0 || amount <= 0 || part <= 0) return 0;
        var value = ((Int128)amount * part * 2 + whole) / ((Int128)whole * 2);
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    // Guests over room lines: one guest per line first (when there are enough), the rest by free capacity
    // (units x per-unit capacity), each line kept within its capacity while the party fits.
    public static int[] SpreadGuests(int guests, IReadOnlyList<int> units, IReadOnlyList<int> capacityPerUnit)
    {
        var n = units.Count;
        var result = new int[n];
        if (n == 0 || guests <= 0) return result;

        var caps = Enumerable.Range(0, n).Select(i => Math.Max(0, units[i]) * Math.Max(1, capacityPerUnit[i])).ToArray();
        var live = Enumerable.Range(0, n).Where(i => units[i] > 0).ToList();
        if (live.Count == 0) return result;

        var baseOne = guests >= live.Count;
        if (baseOne) foreach (var i in live) result[i] = 1;
        var left = guests - (baseOne ? live.Count : 0);
        var weights = live.Select(i => (long)Math.Max(0, caps[i] - result[i])).ToList();
        var share = Apportion(left, weights, weights.Select(w => (int)w).ToList());
        for (var k = 0; k < live.Count; k++) result[live[k]] += share[k];
        return result;
    }

    // Integer per-unit prices q_i >= 1 with sum(units_i x q_i) == target exactly, closest to the price ratio of `prices`
    // (min sum units_i x |q_i - exact_i|; ties: fewer moves, then the first found). The exact shares are floored and the
    // leftover rupees are placed by a DP over sum(units_i x c_i) = leftover with |c_i| <= sum(units): a solution always
    // exists because gcd(units) divides the target (target = price x total units). O(k x U^2 x U), U = sum(units) <= MaxUnits.
    // Null only when no positive solution exists in range (callers then use the flat price on every line).
    public static int[]? UnitPrices(long target, IReadOnlyList<int> units, IReadOnlyList<int> prices)
    {
        var k = units.Count;
        if (k == 0 || k != prices.Count || target <= 0 || units.Any(u => u <= 0) || prices.Any(p => p <= 0 || p > StayLimits.MaxPrice)) return null;

        long unitSum = units.Sum(u => (long)u);
        if (unitSum > MaxUnits || target > (long)StayLimits.MaxPrice * unitSum) return null;
        long offered = 0;
        for (var i = 0; i < k; i++) offered += (long)units[i] * prices[i];

        // exact_i = prices_i x target / offered; cost uses the exact numerator so ties are integer-exact.
        var floors = new long[k];
        long placed = 0;
        for (var i = 0; i < k; i++)
        {
            floors[i] = (long)((Int128)prices[i] * target / offered);
            placed += units[i] * floors[i];
        }
        var deficit = target - placed;

        var bound = (int)unitSum;
        var span = (long)bound * unitSum;
        if (deficit < -span || deficit > span) return null;
        var width = (int)(2 * span + 1);

        // q x offered <= 5e8 x 5e8 fits long; |q x offered - p x target| <= (|c| + 1) x offered, so costs stay far below long.
        var cost = new long[k + 1][];
        var moves = new long[k + 1][];
        var pick = new int[k + 1][];
        for (var j = 0; j <= k; j++)
        {
            cost[j] = new long[width];
            moves[j] = new long[width];
            pick[j] = new int[width];
            Array.Fill(cost[j], long.MaxValue);
        }
        cost[0][span] = 0;

        for (var j = 0; j < k; j++)
        {
            var u = units[j];
            var exactNum = (long)prices[j] * target;
            for (var s = 0; s < width; s++)
            {
                if (cost[j][s] == long.MaxValue) continue;
                for (var c = -bound; c <= bound; c++)
                {
                    var q = floors[j] + c;
                    if (q < 1) continue;
                    var ns = s + (long)c * u;
                    if (ns < 0 || ns >= width) continue;
                    var diff = Math.Abs(q * offered - exactNum);
                    var nc = cost[j][s] + diff * u;
                    var nm = moves[j][s] + Math.Abs(c);
                    var t = (int)ns;
                    if (nc < cost[j + 1][t] || (nc == cost[j + 1][t] && nm < moves[j + 1][t]))
                    {
                        cost[j + 1][t] = nc;
                        moves[j + 1][t] = nm;
                        pick[j + 1][t] = c;
                    }
                }
            }
        }

        var end = (int)(span + deficit);
        if (cost[k][end] == long.MaxValue) return null;

        var result = new int[k];
        var at = end;
        for (var j = k; j >= 1; j--)
        {
            var c = pick[j][at];
            var q = floors[j - 1] + c;
            if (q > int.MaxValue) return null;
            result[j - 1] = (int)q;
            at -= c * units[j - 1];
        }
        return result;
    }
}

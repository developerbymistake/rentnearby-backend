namespace RentNearBy.Core.Models;

// Chains of 2..MaxCars cars from -> to, by hop-bounded label-setting rounds (RAPTOR style): round k holds, per place, the
// K best ways to stand there after exactly k cars, ordered by (fare, road minutes, place path). A place is relaxed only when
// it can still reach `to` with the cars left (backward hop distances), so the work is bounded by the links around both
// ends: no name order and no expansion cap decide what is found. Ways that use the same vehicles in the same order collapse
// to the cheapest change place. A plan with more cars is kept only when no plan with fewer cars is as cheap and as quick.
public static class TaxiWaysChainSearch
{
    private sealed class Label
    {
        public required string Place { get; init; }
        public required int Cars { get; init; }
        public required int Fare { get; init; }
        public int? Minutes { get; init; }
        public Label? Parent { get; init; }
        public Guid? Route { get; init; }
        public required string Path { get; init; }
        public required string Routes { get; init; }

        public bool Visits(string key)
        {
            for (var l = this; l != null; l = l.Parent)
                if (l.Place == key) return true;
            return false;
        }

        public List<string> Places()
        {
            var keys = new List<string>();
            for (var l = this; l != null; l = l.Parent) keys.Add(l.Place);
            keys.Reverse();
            return keys;
        }
    }

    public static int LabelsPerPlace(TaxiWaysOptions o) => Math.Max(1, o.MaxPlans) * 4;

    public static (List<TaxiWaysPlan> Plans, int Relaxed) Plans(
        TaxiWaysNetworkSnapshot s, TaxiWaysAnchor from, TaxiWaysAnchor to, int people, TaxiWaysOptions o)
    {
        var maxCars = o.MaxCars;
        if (maxCars < 2 || from.Key == to.Key) return (new List<TaxiWaysPlan>(), 0);
        var keep = LabelsPerPlace(o);

        var anchors = new Dictionary<string, TaxiWaysAnchor>(StringComparer.Ordinal) { [from.Key] = from, [to.Key] = to };
        TaxiWaysAnchor? AnchorFor(string key)
        {
            if (anchors.TryGetValue(key, out var a)) return a;
            return s.Places.TryGetValue(key, out var p) ? anchors[key] = TaxiWaysSearchEngine.AnchorOf(p) : null;
        }

        var toNeighbours = TaxiWaysSearchEngine.Neighbours(s, to).Where(k => k != from.Key).ToHashSet(StringComparer.Ordinal);
        var hops = HopsTo(s, to.Key, from.Key, toNeighbours, maxCars - 1);

        IEnumerable<string> Next(string u)
        {
            IEnumerable<string> keys = u == from.Key
                ? TaxiWaysSearchEngine.Neighbours(s, from)
                : s.LinksAt(u).Select(l => l.OtherEnd(u));
            if (u != from.Key && !to.Resolved && toNeighbours.Contains(u)) keys = keys.Append(to.Key);
            return keys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);
        }

        var legs = new Dictionary<(string, string, Guid?), TaxiWaysLegResult?>();
        TaxiWaysLegResult? Leg(string u, string v, Guid? exclude)
        {
            if (legs.TryGetValue((u, v, exclude), out var cached)) return cached;
            var a = AnchorFor(u);
            var b = AnchorFor(v);
            var leg = a == null || b == null ? null
                : TaxiWaysSearchEngine.BuildLeg(s, a, b, 0, people, o, basedHereFirst: u == from.Key && from.MyLocation, excludeRoute: exclude);
            return legs[(u, v, exclude)] = leg;
        }

        var relaxed = 0;
        var arrivals = new List<Label>();
        var current = new Dictionary<string, List<Label>>(StringComparer.Ordinal)
        {
            [from.Key] = [new Label { Place = from.Key, Cars = 0, Fare = 0, Minutes = 0, Path = from.Key, Routes = string.Empty }],
        };
        for (var k = 1; k <= maxCars && current.Count > 0; k++)
        {
            var next = new Dictionary<string, List<Label>>(StringComparer.Ordinal);
            foreach (var (u, labels) in current.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                foreach (var v in Next(u))
                {
                    if (v == from.Key || !hops.TryGetValue(v, out var left) || k + left > maxCars) continue;
                    if (v == to.Key && k == 1) continue;
                    foreach (var label in labels)
                    {
                        if (label.Visits(v)) continue;
                        relaxed++;
                        var leg = Leg(u, v, label.Route);
                        if (leg == null) continue;
                        var route = leg.Top.Link.RouteId;
                        var arrived = new Label
                        {
                            Place = v, Cars = k, Fare = label.Fare + leg.ListedFare,
                            Minutes = label.Minutes is { } m && leg.Minutes is { } lm ? m + lm : null,
                            Parent = label, Route = route, Path = label.Path + "~" + v,
                            Routes = label.Routes + ">" + route.ToString("N"),
                        };
                        if (v == to.Key) arrivals.Add(arrived);
                        else
                        {
                            if (!next.TryGetValue(v, out var list)) next[v] = list = new();
                            list.Add(arrived);
                        }
                    }
                }
            }
            foreach (var key in next.Keys.ToList()) next[key] = Best(next[key], keep);
            current = next;
        }

        var chosen = Front(Best(arrivals, int.MaxValue)).Take(o.MaxPlans).ToList();
        var plans = new List<TaxiWaysPlan>();
        foreach (var label in chosen)
        {
            var stops = label.Places().Select(AnchorFor).OfType<TaxiWaysAnchor>().ToList();
            if (stops.Count != label.Cars + 1) continue;
            var plan = TaxiWaysSearchEngine.BuildPlan(s, stops, people, o);
            if (plan != null) plans.Add(plan);
        }
        return (plans, relaxed);
    }

    // Fewest cars from each place to `to` (0 at `to`), up to maxHops; `from` is never a middle place.
    private static Dictionary<string, int> HopsTo(
        TaxiWaysNetworkSnapshot s, string toKey, string fromKey, IReadOnlySet<string> toNeighbours, int maxHops)
    {
        var hops = new Dictionary<string, int>(StringComparer.Ordinal) { [toKey] = 0 };
        var frontier = new List<string>();
        foreach (var k in toNeighbours.OrderBy(x => x, StringComparer.Ordinal))
            if (maxHops >= 1 && hops.TryAdd(k, 1)) frontier.Add(k);
        for (var depth = 2; depth <= maxHops && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var p in frontier)
                foreach (var l in s.LinksAt(p))
                {
                    var q = l.OtherEnd(p);
                    if (q != fromKey && hops.TryAdd(q, depth)) next.Add(q);
                }
            frontier = next;
        }
        return hops;
    }

    // One label per vehicle sequence (the cheapest change place), then the `keep` best by fare, minutes, path.
    private static List<Label> Best(List<Label> labels, int keep) =>
        labels
            .OrderBy(l => l.Fare).ThenBy(l => l.Minutes ?? int.MaxValue).ThenBy(l => l.Path, StringComparer.Ordinal)
            .DistinctBy(l => l.Routes, StringComparer.Ordinal)
            .Take(keep)
            .ToList();

    // Ordered by cars, fare, minutes, path; a way with more cars stays only when every way with fewer cars costs more,
    // or is slower (minutes compared only when both are known).
    private static IEnumerable<Label> Front(List<Label> arrivals)
    {
        var ordered = arrivals
            .OrderBy(l => l.Cars).ThenBy(l => l.Fare).ThenBy(l => l.Minutes ?? int.MaxValue).ThenBy(l => l.Path, StringComparer.Ordinal)
            .ToList();
        foreach (var p in ordered)
        {
            var dominated = ordered.Any(q => q.Cars < p.Cars && q.Fare <= p.Fare
                && (q.Minutes == null || p.Minutes == null || q.Minutes <= p.Minutes));
            if (!dominated) yield return p;
        }
    }
}

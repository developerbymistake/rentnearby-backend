namespace RentNearBy.Core.Models;

// One vehicle on offer for a stretch: Rank = its position in the stretch's ranked list (0 = top).
public readonly record struct SeatOption(int Rank, short? Seats, int Fare);

// Chosen = ranks of the distinct vehicles that carry the group, best ranked first.
// Covered false: the listed vehicles cannot seat the group; the top vehicle is counted once per started group of its seats.
public sealed record SeatCoverResult(IReadOnlyList<int> Chosen, int Fare, int Cars, bool Covered)
{
    public static readonly SeatCoverResult None = new([], 0, 0, false);
}

// Least-cost set of distinct vehicles whose seats hold the group (0/1 covering knapsack over capacity 0..people).
// Order: lowest fare, then fewer cars, then the better ranked set. O(vehicles x people).
public static class TaxiSeatCover
{
    public static SeatCoverResult Solve(IReadOnlyList<SeatOption> options, int people)
    {
        if (options.Count == 0) return SeatCoverResult.None;
        var top = options[0];
        if (people <= 0 || top.Seats is { } s0 && s0 >= people)
            return new SeatCoverResult([top.Rank], top.Fare, 1, true);

        var best = new State?[people + 1];
        best[0] = new State(0, []);
        foreach (var o in options.OrderBy(x => x.Rank))
        {
            if (o.Seats is not { } seats || seats <= 0 || o.Fare < 0) continue;
            var add = Math.Min((int)seats, people);
            for (var c = people - 1; c >= 0; c--)
            {
                if (best[c] is not { } from) continue;
                var next = new State(from.Fare + o.Fare, [.. from.Chosen, o.Rank]);
                var at = Math.Min(people, c + add);
                if (best[at] is not { } current || Better(next, current)) best[at] = next;
            }
        }

        if (best[people] is { } cover)
            return new SeatCoverResult(cover.Chosen, cover.Fare, cover.Chosen.Count, true);

        var cars = TaxiWaysRanking.CarsNeeded(people, top.Seats);
        return new SeatCoverResult([top.Rank], top.Fare * cars, cars, false);
    }

    private sealed record State(int Fare, List<int> Chosen);

    private static bool Better(State a, State b)
    {
        if (a.Fare != b.Fare) return a.Fare < b.Fare;
        if (a.Chosen.Count != b.Chosen.Count) return a.Chosen.Count < b.Chosen.Count;
        for (var i = 0; i < a.Chosen.Count; i++)
            if (a.Chosen[i] != b.Chosen[i]) return a.Chosen[i] < b.Chosen[i];
        return false;
    }
}

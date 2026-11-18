namespace RentNearBy.Core.Models.TaxiRequests;

// Dates a request occupies (P5): first needed day to last needed day, both included.
public readonly record struct TaxiDateRange(DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber + 1;

    public bool IsValid => From <= To;

    // Inclusive on both ends: back to back ranges (to = next from) overlap, a gap of a day does not.
    public bool Overlaps(TaxiDateRange other) => Overlaps(From, To, other.From, other.To);

    public static bool Overlaps(DateOnly aFrom, DateOnly aTo, DateOnly bFrom, DateOnly bTo) => aFrom <= bTo && bFrom <= aTo;

    // Pickup = first trip day, drop = last trip day, sightseeing = its first and last chosen plan day (1 based).
    // Returns null when no section is on, or a sightseeing day is empty or outside 1..dayCount.
    public static TaxiDateRange? Occupied(bool pickup, bool sightseeing, bool drop, DateOnly tripStart, int dayCount, IReadOnlyCollection<int> sightseeingDays)
    {
        if (dayCount < 1) return null;
        int? first = null, last = null;
        void Take(int day)
        {
            first = first is null ? day : Math.Min(first.Value, day);
            last = last is null ? day : Math.Max(last.Value, day);
        }
        if (pickup) Take(1);
        if (drop) Take(dayCount);
        if (sightseeing)
        {
            if (sightseeingDays.Count == 0 || sightseeingDays.Any(d => d < 1 || d > dayCount)) return null;
            Take(sightseeingDays.Min());
            Take(sightseeingDays.Max());
        }
        if (first is null || last is null) return null;
        return new TaxiDateRange(tripStart.AddDays(first.Value - 1), tripStart.AddDays(last.Value - 1));
    }
}

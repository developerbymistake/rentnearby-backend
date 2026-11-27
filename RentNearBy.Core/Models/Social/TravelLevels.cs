namespace RentNearBy.Core.Models.Social;

// Max null = no upper bound (the highest level).
public sealed record TravelLevel(int Index, int Min, int? Max, string Name);

// Travel levels (primary blue + levels spec B): computed from Users.CompletedTripsCount on every read, never stored.
// The app keeps the same table (lib/features/social/shared/travel_levels.dart); a test on each side pins the values.
public static class TravelLevels
{
    public const int Lowest = 0;
    public const int Highest = 6;

    public static readonly IReadOnlyList<TravelLevel> All =
    [
        new(0, 0, 0, "New Traveller"),
        new(1, 1, 9, "Trail Starter"),
        new(2, 10, 29, "Wanderer"),
        new(3, 30, 79, "Trekker"),
        new(4, 80, 199, "Pathfinder"),
        new(5, 200, 499, "Summit Seeker"),
        new(6, 500, null, "Travel King"),
    ];

    public static int Level(int completedTrips)
    {
        for (var i = All.Count - 1; i > Lowest; i--)
            if (completedTrips >= All[i].Min) return i;
        return Lowest;
    }

    public static string Name(int level) => All[Math.Clamp(level, Lowest, Highest)].Name;

    // The level the count has just reached (the count is exactly the first number of level 1..6), else null.
    public static int? ReachedAt(int newCount)
    {
        if (newCount < 1) return null;
        var level = Level(newCount);
        return level > Level(newCount - 1) ? level : null;
    }
}

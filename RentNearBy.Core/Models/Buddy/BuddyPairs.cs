namespace RentNearBy.Core.Models.Buddy;

// Pair order = PostgreSQL uuid order (lowercase text order), so PairLowId < PairHighId holds in the database too.
public static class BuddyPairs
{
    public static (Guid Low, Guid High) Order(Guid a, Guid b)
        => TripExpenseEngine.CompareIds(a, b) <= 0 ? (a, b) : (b, a);

    public static string PairLockKey(Guid a, Guid b)
    {
        var (low, high) = Order(a, b);
        return $"buddy:pair:{low:D}:{high:D}";
    }

    public static string SenderLockKey(Guid userId) => $"buddy:sender:{userId:D}";

    public static string TripSlotsLockKey(Guid userId) => $"trip:slots:{userId:D}";
}

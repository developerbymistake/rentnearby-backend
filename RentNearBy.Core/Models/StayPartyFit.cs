namespace RentNearBy.Core.Models;

// One room type of a stay as the quote sees it. MaxGuests 4 = "4+", counted as 4 per room.
public sealed record StayFitRoom(string RoomName, int MaxGuests, int? PricePerNight, short? Floor);

public sealed record StayFitLine(string RoomName, int Units, int Guests, int? PricePerNight);

public sealed record StayFitQuote(bool Fits, IReadOnlyList<StayFitLine> Lines, int? PerNight, int? Total);

// Which rooms seat the party, at the lowest price. Room types are unlimited in units (the host decides availability).
public static class StayPartyFit
{
    // A room seats at most MaxRoomCapacity ("4+"), so MaxRoomsWanted rooms can always seat MaxGuests.
    public const int MaxRoomCapacity = 4, MaxRoomsWanted = 25, MaxGuests = MaxRoomsWanted * MaxRoomCapacity;

    public static int CapacityOf(int maxGuests) => Math.Max(1, maxGuests);

    // Cheapest unit counts over the room types: sum(units) = roomsWanted (null = any count from 1 to guests),
    // sum(units x capacity) >= guests; ties: fewer rooms, then more units on earlier types. Guests spread by capacity
    // (MoneySplit.SpreadGuests). Exact enumeration of compositions: O(C(r + k - 1, k - 1)) with k <= StayLimits.MaxRooms.
    public static StayFitQuote Quote(IReadOnlyList<StayFitRoom> rooms, int guests, int? roomsWanted, bool groundFloorOnly, int nights = 1)
    {
        var pool = rooms.Where(r => !groundFloorOnly || r.Floor == StayNearbyRooms.GroundFloor).ToList();
        guests = Math.Clamp(guests, 1, MaxGuests);
        if (pool.Count == 0) return new StayFitQuote(false, [], null, null);

        var caps = pool.Select(r => CapacityOf(r.MaxGuests)).ToList();
        var priced = pool.All(r => r.PricePerNight is > 0);
        var prices = pool.Select(r => priced ? (long)r.PricePerNight!.Value : 0L).ToList();

        int[]? best = null;
        if (roomsWanted is { } wanted)
            best = Choose(caps, prices, Math.Clamp(wanted, 1, MaxRoomsWanted), guests, null);
        else
        {
            var minRooms = (int)Math.Ceiling(guests / (double)caps.Max());
            long bestCost = long.MaxValue;
            for (var r = Math.Max(1, minRooms); r <= Math.Min(guests, MaxRoomsWanted); r++)
            {
                var units = Choose(caps, prices, r, guests, null);
                if (units == null) continue;
                var cost = Cost(units, prices);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = units;
                }
                if (!priced) break;
            }
        }

        if (best == null) return new StayFitQuote(false, [], null, null);
        return Build(pool, best, caps, guests, priced, nights);
    }

    // Unit counts u_i (sum = rooms, sum u_i x caps_i >= guests) minimising (deviation from baseline, cost),
    // then more units on earlier lines. Null when no count seats the party.
    public static int[]? Choose(IReadOnlyList<int> caps, IReadOnlyList<long> prices, int rooms, int guests, IReadOnlyList<int>? baseline)
    {
        var k = caps.Count;
        if (k == 0 || rooms < 1) return null;
        int[]? best = null;
        (long Dev, long Cost) bestKey = (long.MaxValue, long.MaxValue);
        var units = new int[k];

        void Walk(int i, int left)
        {
            if (i == k - 1)
            {
                units[i] = left;
                long capacity = 0;
                for (var j = 0; j < k; j++) capacity += (long)units[j] * caps[j];
                if (capacity < guests) return;
                long dev = 0;
                if (baseline != null)
                    for (var j = 0; j < k; j++) dev += Math.Abs(units[j] - baseline[j]);
                var cost = Cost(units, prices);
                // Walk visits more-units-first on earlier lines, so a strict improvement keeps the earliest tie.
                if (dev < bestKey.Dev || (dev == bestKey.Dev && cost < bestKey.Cost))
                {
                    bestKey = (dev, cost);
                    best = (int[])units.Clone();
                }
                return;
            }
            for (var u = left; u >= 0; u--)
            {
                units[i] = u;
                Walk(i + 1, left - u);
            }
        }

        Walk(0, rooms);
        return best;
    }

    private static long Cost(IReadOnlyList<int> units, IReadOnlyList<long> prices)
    {
        long sum = 0;
        for (var i = 0; i < units.Count; i++) sum += units[i] * prices[i];
        return sum;
    }

    private static StayFitQuote Build(List<StayFitRoom> pool, int[] units, List<int> caps, int guests, bool priced, int nights)
    {
        var spread = MoneySplit.SpreadGuests(guests, units, caps);
        var lines = new List<StayFitLine>();
        long perNight = 0;
        for (var i = 0; i < pool.Count; i++)
        {
            if (units[i] == 0) continue;
            lines.Add(new StayFitLine(pool[i].RoomName, units[i], spread[i], pool[i].PricePerNight));
            if (priced) perNight += (long)units[i] * pool[i].PricePerNight!.Value;
        }
        if (!priced) return new StayFitQuote(true, lines, null, null);
        var per = TripExecutionRules.Saturate(perNight);
        return new StayFitQuote(true, lines, per, TripExecutionRules.Saturate(perNight * Math.Max(1, nights)));
    }
}

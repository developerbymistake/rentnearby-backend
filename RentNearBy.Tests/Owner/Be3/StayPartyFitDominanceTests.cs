namespace RentNearBy.Tests.Owner.Be3;

// Room types now go up to the ceiling of 8 (contract 1.5). The quote keeps only the cheapest room type of each capacity before the
// exact enumeration; this proves the answer equals the full enumeration over every room type (same lines, same total).
public class StayPartyFitDominanceTests
{
    private static readonly string[] Names = ["Standard", "Deluxe", "Premium", "Suite", "Family Room", "Executive", "Dormitory", "Cottage"];

    private static StayFitQuote Reference(IReadOnlyList<StayFitRoom> pool, int guests, int? roomsWanted, int nights)
    {
        var caps = pool.Select(r => StayPartyFit.CapacityOf(r.MaxGuests)).ToList();
        var priced = pool.All(r => r.PricePerNight is > 0);
        var prices = pool.Select(r => priced ? (long)r.PricePerNight!.Value : 0L).ToList();
        int[]? best = null;
        if (roomsWanted is { } w) best = StayPartyFit.Choose(caps, prices, w, guests, null);
        else
        {
            var bestCost = long.MaxValue;
            for (var r = Math.Max(1, (int)Math.Ceiling(guests / (double)caps.Max())); r <= Math.Min(guests, StayPartyFit.MaxRoomsWanted); r++)
            {
                var units = StayPartyFit.Choose(caps, prices, r, guests, null);
                if (units == null) continue;
                var cost = units.Select((u, i) => u * prices[i]).Sum();
                if (cost < bestCost) { bestCost = cost; best = units; }
                if (!priced) break;
            }
        }
        if (best == null) return new StayFitQuote(false, [], null, null);
        var lines = pool.Select((room, i) => (room, i)).Where(x => best[x.i] > 0).Select(x => (x.room.RoomName, best[x.i])).ToList();
        var perNight = priced ? (int?)pool.Select((room, i) => best[i] * room.PricePerNight!.Value).Sum() : null;
        return new StayFitQuote(true, lines.Select(l => new StayFitLine(l.RoomName, l.Item2, 0, null)).ToList(), perNight, perNight * nights);
    }

    [Fact]
    public void Collapsed_quote_equals_the_full_enumeration_on_random_stays()
    {
        var rng = new Random(20261121);
        for (var run = 0; run < 400; run++)
        {
            var count = rng.Next(1, 7);
            var rooms = Enumerable.Range(0, count)
                .Select(i => new StayFitRoom(Names[i], rng.Next(1, 5), rng.Next(0, 4) == 0 ? 2000 : rng.Next(1, 8) * 500, 0))
                .ToList();
            var guests = rng.Next(1, 9);
            int? wanted = rng.Next(0, 3) == 0 ? rng.Next(1, 5) : null;
            var got = StayPartyFit.Quote(rooms, guests, wanted, groundFloorOnly: false, nights: 2);
            var want = Reference(rooms, guests, wanted, 2);
            Assert.Equal(want.Fits, got.Fits);
            Assert.Equal(want.PerNight, got.PerNight);
            Assert.Equal(want.Lines.Select(l => (l.RoomName, l.Units)), got.Lines.Select(l => (l.RoomName, l.Units)));
        }
    }

    [Fact]
    public void Eight_room_types_and_a_big_party_stay_fast()
    {
        var rooms = Names.Select((n, i) => new StayFitRoom(n, 1 + i % 4, 1000 + 100 * i, 0)).ToList();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 50; i++) Assert.True(StayPartyFit.Quote(rooms, 20, null, false).Fits);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"50 quotes took {watch.ElapsedMilliseconds} ms");
    }
}

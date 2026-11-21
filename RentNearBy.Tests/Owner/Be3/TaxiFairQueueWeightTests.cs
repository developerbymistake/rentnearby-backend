using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.Owner.Be3;

// Contract 7.1: weight 2 gets about twice the turns of weight 1, nobody starves, a new driver enters at the floor, all weight 1 =
// today's order; Fits sees only the routes the matcher loaded (visible ones).
public class TaxiFairQueueWeightTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 6, 0, 0, DateTimeKind.Utc);

    private static FairQueueRequest Request(int n)
        => new(Guid.NewGuid(), Now.AddSeconds(n), 3, 0, 0, 4, 2, 1, new TaxiDateRange(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 2)),
            Guid.NewGuid(), new HashSet<Guid>(), new HashSet<Guid>());

    // One ring per round for one request: the driver the queue picks.
    private static Dictionary<Guid, int> Run(IReadOnlyList<FairQueueDriver> start, int rounds)
    {
        var pool = start.ToList();
        var city = new FairQueueCity(0, 0);
        var picks = pool.ToDictionary(d => d.ListingId, _ => 0);
        for (var i = 0; i < rounds; i++)
        {
            var req = Request(i) with { AcceptTarget = 1 };
            var result = TaxiFairQueue.Pick(new PickInput(city, [req], pool), Now.AddMinutes(i));
            foreach (var o in result.Offers) picks[o.DriverListingId]++;
            var byId = result.Drivers.ToDictionary(u => u.ListingId);
            pool = pool.Select(d => d with { Pass = byId[d.ListingId].Pass, SeenSeq = byId[d.ListingId].SeenSeq, LastOfferedAt = byId[d.ListingId].LastOfferedAt }).ToList();
            city = new FairQueueCity(result.CitySeq, result.CityLastMinPass);
        }
        return picks;
    }

    [Fact]
    public void Weight_two_gets_about_twice_the_turns_over_a_thousand_rounds_and_nobody_starves()
    {
        var plus = RingWeightTests.Driver(2);
        var free1 = RingWeightTests.Driver(1);
        var free2 = RingWeightTests.Driver(1);
        var picks = Run([plus, free1, free2], 1000);
        Assert.InRange(picks[plus.ListingId], 480, 520);
        Assert.InRange(picks[free1.ListingId], 230, 270);
        Assert.InRange(picks[free2.ListingId], 230, 270);
    }

    [Fact]
    public void All_weight_one_is_the_release_one_round_robin()
    {
        var drivers = Enumerable.Range(0, 4).Select(_ => RingWeightTests.Driver(1)).ToList();
        var picks = Run(drivers, 400);
        Assert.All(picks.Values, v => Assert.Equal(100, v));
    }

    [Fact]
    public void A_new_driver_enters_at_the_floor_not_at_zero()
    {
        var a = RingWeightTests.Driver(1) with { Pass = 5 * TaxiFairQueue.Stride, SeenSeq = 3 };
        var b = RingWeightTests.Driver(2) with { Pass = 0, SeenSeq = 0 };
        var result = TaxiFairQueue.Pick(new PickInput(new FairQueueCity(3, 5 * TaxiFairQueue.Stride), [Request(0) with { AcceptTarget = 1 }], [a, b]), Now);
        var bPass = result.Drivers.Single(u => u.ListingId == b.ListingId).Pass;
        Assert.True(bPass >= 5 * TaxiFairQueue.Stride);
    }

    [Fact]
    public void Fits_matches_only_the_routes_it_was_given()
    {
        var visibleOnly = RingWeightTests.Driver(2) with { Routes = [new TaxiRoutePair(1, 99)] };
        Assert.False(TaxiFairQueue.Fits(Request(0), visibleOnly));
        Assert.True(TaxiFairQueue.Fits(Request(0), visibleOnly with { Routes = [new TaxiRoutePair(1, 2)] }));
    }
}

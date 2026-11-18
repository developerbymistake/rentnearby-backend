using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class TaxiFairQueuePickTests
{
    private const long To = 1001;
    private const long From = 2002;
    private const long S = TaxiFairQueue.Stride;
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 12, 18);

    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static FairQueueDriver Driver(int n, long pass = 0, long seenSeq = 5, DateTime? lastOffered = null,
        bool ringing = false, bool selected = false, int seats = 7, TaxiRoutePair[]? routes = null, TaxiDateRange[]? occupied = null)
        => new(G(n), G(1000 + n), pass, seenSeq, lastOffered, ringing, selected, seats,
            routes ?? [new TaxiRoutePair(From, To)], occupied ?? []);

    private static FairQueueRequest Request(int n, int minute = 0, int target = 3, int accepted = 0, int ringing = 0,
        int seats = 4, long? from = null, TaxiDateRange? dates = null, Guid[]? offered = null, Guid[]? blocked = null, Guid? traveller = null)
        => new(G(500 + n), Now.AddMinutes(minute), target, accepted, ringing, seats, To, from,
            dates ?? new TaxiDateRange(Day, Day.AddDays(2)), traveller ?? G(900 + n),
            new HashSet<Guid>(offered ?? []), new HashSet<Guid>(blocked ?? []));

    private static PickResult Run(FairQueueRequest[] requests, FairQueueDriver[] pool, long seq = 5, long lastMinPass = 0, int maxOffers = TaxiFairQueue.MaxOffersPerPass)
        => TaxiFairQueue.Pick(new PickInput(new FairQueueCity(seq, lastMinPass), requests, pool, maxOffers), Now);

    private static Guid[] OffersOf(PickResult r, FairQueueRequest req)
        => r.Offers.Where(o => o.RequestId == req.Id).Select(o => o.DriverListingId).ToArray();

    private static FairQueueDriverUpdate UpdateOf(PickResult r, int n) => r.Drivers.Single(d => d.ListingId == G(n));

    private static FairQueueRequestResult ResultOf(PickResult r, FairQueueRequest req) => r.Requests.Single(x => x.RequestId == req.Id);

    [Fact]
    public void Oldest_request_is_served_first()
    {
        var younger = Request(1, minute: 5, target: 1);
        var older = Request(2, minute: 1, target: 1);
        var r = Run([younger, older], [Driver(1, pass: 0), Driver(2, pass: S)]);
        Assert.Equal([G(1)], OffersOf(r, older));
        Assert.Equal([G(2)], OffersOf(r, younger));
        Assert.Equal(older.Id, r.Offers[0].RequestId);
    }

    [Theory]
    [InlineData(3, 0, 0, 3)]
    [InlineData(3, 1, 0, 2)]
    [InlineData(3, 1, 1, 1)]
    [InlineData(3, 2, 1, 0)]
    [InlineData(3, 3, 0, 0)]
    [InlineData(6, 2, 0, 3)]
    [InlineData(6, 2, 3, 0)]
    [InlineData(6, 2, 2, 1)]
    [InlineData(9, 0, 3, 0)]
    [InlineData(9, 0, 1, 2)]
    [InlineData(9, 7, 1, 1)]
    [InlineData(2, 3, 0, 0)]
    public void Need_is_three_minus_accepts_minus_ringing(int target, int accepted, int ringing, int expected)
    {
        var req = Request(1, target: target, accepted: accepted, ringing: ringing);
        var pool = Enumerable.Range(1, 6).Select(i => Driver(i)).ToArray();
        var r = Run([req], pool);
        Assert.Equal(expected, req.Need);
        Assert.Equal(expected, OffersOf(r, req).Length);
        Assert.Equal(expected == 0 ? FairQueueOutcome.NoNeed : FairQueueOutcome.Served, ResultOf(r, req).Outcome);
    }

    [Fact]
    public void Need_never_lets_live_rings_exceed_three()
    {
        var pool = Enumerable.Range(1, 9).Select(i => Driver(i)).ToArray();
        for (var target = 1; target <= TaxiDispatchRules.MaxAccepts; target++)
        for (var accepted = 0; accepted <= target; accepted++)
        for (var ringing = 0; ringing <= TaxiDispatchRules.MaxLiveRings; ringing++)
        {
            var req = Request(1, target: target, accepted: accepted, ringing: ringing);
            var picked = OffersOf(Run([req], pool), req).Length;
            Assert.True(ringing + picked <= Math.Max(ringing, Math.Min(TaxiDispatchRules.MaxLiveRings, target - accepted)),
                $"target {target} accepted {accepted} ringing {ringing} picked {picked}");
            Assert.Equal(Math.Max(0, Math.Min(TaxiDispatchRules.MaxLiveRings, target - accepted) - ringing), picked);
        }
    }

    [Fact]
    public void Driver_with_a_live_ring_is_skipped_and_keeps_his_pass()
    {
        var req = Request(1, target: 1);
        var r = Run([req], [Driver(1, pass: 0, ringing: true), Driver(2, pass: 3 * S)]);
        Assert.Equal([G(2)], OffersOf(r, req));
        var skipped = UpdateOf(r, 1);
        Assert.Equal(0, skipped.Pass);
        Assert.False(skipped.Picked);
        Assert.Equal(6, skipped.SeenSeq);

        var next = Request(2, target: 1);
        var r2 = Run([next], [Driver(1, pass: skipped.Pass, seenSeq: r.CitySeq), Driver(2, pass: UpdateOf(r, 2).Pass, seenSeq: r.CitySeq, lastOffered: Now)], seq: r.CitySeq, lastMinPass: r.CityLastMinPass);
        Assert.Equal([G(1)], OffersOf(r2, next));
    }

    [Fact]
    public void Driver_with_a_pending_selection_is_skipped()
    {
        var req = Request(1, target: 1);
        var r = Run([req], [Driver(1, pass: 0, selected: true), Driver(2, pass: S)]);
        Assert.Equal([G(2)], OffersOf(r, req));
        Assert.Equal(0, UpdateOf(r, 1).Pass);
    }

    [Fact]
    public void Accepted_driver_is_still_free()
    {
        var accepted = Driver(1, pass: 0);
        Assert.True(accepted.IsFree);
        var req = Request(2, target: 1);
        var r = Run([req], [accepted, Driver(2, pass: S)]);
        Assert.Equal([G(1)], OffersOf(r, req));
    }

    [Fact]
    public void Already_offered_driver_is_never_offered_again()
    {
        var req = Request(1, target: 3, accepted: 1, offered: [G(1)]);
        var r = Run([req], [Driver(1, pass: 0), Driver(2, pass: S), Driver(3, pass: 2 * S)]);
        Assert.Equal([G(2), G(3)], OffersOf(r, req));
    }

    [Fact]
    public void Overlapping_booking_excludes_only_that_request()
    {
        var booked = new TaxiDateRange(Day.AddDays(2), Day.AddDays(4));
        var clash = Request(1, minute: 0, target: 1, dates: new TaxiDateRange(Day, Day.AddDays(2)));
        var clear = Request(2, minute: 1, target: 1, dates: new TaxiDateRange(Day.AddDays(5), Day.AddDays(6)));
        var r = Run([clash, clear], [Driver(1, pass: 0, occupied: [booked]), Driver(2, pass: S)]);
        Assert.Equal([G(2)], OffersOf(r, clash));
        Assert.Equal([G(1)], OffersOf(r, clear));
    }

    [Fact]
    public void Seats_and_route_filters_apply()
    {
        var big = Request(1, minute: 0, target: 3, seats: 7, from: From);
        var pool = new[]
        {
            Driver(1, pass: 0, seats: 4),
            Driver(2, pass: 0, routes: [new TaxiRoutePair(3003, To)]),
            Driver(3, pass: 0, routes: [new TaxiRoutePair(From, 4004)]),
            Driver(4, pass: 5 * S, seats: 7, routes: [new TaxiRoutePair(3003, To), new TaxiRoutePair(From, To)]),
        };
        var r = Run([big], pool);
        Assert.Equal([G(4)], OffersOf(r, big));

        var anyFrom = Request(2, target: 3, seats: 4);
        var r2 = Run([anyFrom], pool);
        Assert.Equal(new[] { G(1), G(2), G(4) }.Order(), OffersOf(r2, anyFrom).Order());
    }

    [Fact]
    public void Traveller_and_blocked_users_are_not_rung()
    {
        var req = Request(1, target: 3, traveller: G(1001), blocked: [G(1002)]);
        var r = Run([req], [Driver(1), Driver(2), Driver(3, pass: S)]);
        Assert.Equal([G(3)], OffersOf(r, req));
    }

    [Fact]
    public void One_driver_gets_one_ring_per_pass()
    {
        var a = Request(1, minute: 0, target: 3);
        var b = Request(2, minute: 1, target: 3);
        var r = Run([a, b], [Driver(1, pass: 0), Driver(2, pass: S)]);
        Assert.Equal(2, r.Offers.Count);
        Assert.Equal(r.Offers.Count, r.Offers.Select(o => o.DriverListingId).Distinct().Count());
        Assert.Equal(2, OffersOf(r, a).Length);
        Assert.Equal(FairQueueOutcome.Wait, ResultOf(r, b).Outcome);
        Assert.Equal(S, UpdateOf(r, 1).Pass);
        Assert.Equal(2 * S, UpdateOf(r, 2).Pass);
    }

    [Fact]
    public void New_driver_starts_at_the_floor()
    {
        var req = Request(1, target: 1);
        var r = Run([req], [Driver(1, pass: 4 * S, seenSeq: 5, lastOffered: Now.AddHours(-1)), Driver(2, pass: 6 * S, seenSeq: 5), Driver(3, pass: 0, seenSeq: 0, lastOffered: null)]);
        Assert.Equal([G(3)], OffersOf(r, req));
        Assert.Equal(5 * S, UpdateOf(r, 3).Pass);
        Assert.Equal(4 * S, UpdateOf(r, 1).Pass);
    }

    [Fact]
    public void Returning_driver_is_raised_to_the_floor_not_lowered()
    {
        var req = Request(1, target: 3, accepted: 3);
        var r = Run([req], [Driver(1, pass: 4 * S), Driver(2, pass: 6 * S), Driver(3, pass: S, seenSeq: 2), Driver(4, pass: 9 * S, seenSeq: 2)]);
        Assert.Empty(r.Offers);
        Assert.Equal(4 * S, UpdateOf(r, 3).Pass);
        Assert.Equal(9 * S, UpdateOf(r, 4).Pass);
        Assert.All(r.Drivers, d => Assert.Equal(6, d.SeenSeq));
    }

    [Fact]
    public void Empty_continuing_set_uses_the_city_last_min_pass()
    {
        var req = Request(1, target: 1);
        var r = Run([req], [Driver(1, pass: 0, seenSeq: 1), Driver(2, pass: 10 * S, seenSeq: 3)], seq: 5, lastMinPass: 7 * S);
        Assert.Equal(7 * S, UpdateOf(r, 1).Pass - S);
        Assert.Equal(10 * S, UpdateOf(r, 2).Pass);
        Assert.Equal([G(1)], OffersOf(r, req));
        Assert.Equal(8 * S, r.CityLastMinPass);

        var none = Run([req], [], seq: 5, lastMinPass: 7 * S);
        Assert.Equal(7 * S, none.CityLastMinPass);
        Assert.Equal(6, none.CitySeq);
    }

    [Fact]
    public void No_free_but_busy_driver_gives_wait()
    {
        var req = Request(1, target: 3);
        var r = Run([req], [Driver(1, ringing: true), Driver(2, selected: true), Driver(3, seats: 4, routes: [new TaxiRoutePair(From, 4004)])]);
        Assert.Empty(r.Offers);
        Assert.Equal(FairQueueOutcome.Wait, ResultOf(r, req).Outcome);
    }

    [Fact]
    public void No_driver_at_all_gives_exhausted()
    {
        var req = Request(1, target: 3, offered: [G(1)]);
        var r = Run([req], [Driver(1), Driver(2, routes: [new TaxiRoutePair(From, 4004)])]);
        Assert.Empty(r.Offers);
        Assert.Equal(FairQueueOutcome.Exhausted, ResultOf(r, req).Outcome);
        Assert.Equal(FairQueueOutcome.Exhausted, ResultOf(Run([req], []), req).Outcome);
    }

    [Fact]
    public void Ties_break_by_last_offered_then_id()
    {
        var req = Request(1, target: 1);
        var r = Run([req], [Driver(3, lastOffered: Now.AddHours(-1)), Driver(2, lastOffered: null), Driver(1, lastOffered: Now)]);
        Assert.Equal([G(2)], OffersOf(r, req));

        var r2 = Run([req], [Driver(3, lastOffered: Now.AddHours(-1)), Driver(1, lastOffered: Now)]);
        Assert.Equal([G(3)], OffersOf(r2, req));

        var r3 = Run([req], [Driver(3, lastOffered: Now), Driver(2, lastOffered: Now)]);
        Assert.Equal([G(2)], OffersOf(r3, req));
    }

    [Fact]
    public void Offer_cap_defers_the_rest()
    {
        var a = Request(1, minute: 0, target: 3);
        var b = Request(2, minute: 1, target: 3);
        var r = Run([a, b], Enumerable.Range(1, 6).Select(i => Driver(i)).ToArray(), maxOffers: 2);
        Assert.Equal(2, r.Offers.Count);
        Assert.Equal(FairQueueOutcome.Served, ResultOf(r, a).Outcome);
        Assert.Equal(FairQueueOutcome.Deferred, ResultOf(r, b).Outcome);
    }

    [Fact]
    public void Picked_driver_gets_now_and_a_stride_and_weight_is_one()
    {
        var req = Request(1, target: 1);
        var d = Driver(1, pass: 2 * S, lastOffered: Now.AddDays(-1));
        var r = Run([req], [d]);
        Assert.Equal(1, TaxiFairQueue.WeightOf(d));
        Assert.Equal(3 * S, UpdateOf(r, 1).Pass);
        Assert.Equal(Now, UpdateOf(r, 1).LastOfferedAt);
        Assert.True(UpdateOf(r, 1).Picked);
    }

    [Fact]
    public void Same_input_gives_the_same_result()
    {
        var reqs = new[] { Request(2, minute: 1), Request(1, minute: 1), Request(3, minute: 0, target: 2) };
        var pool = Enumerable.Range(1, 5).Select(i => Driver(i, pass: (i % 2) * S, seenSeq: i == 5 ? 0 : 5)).ToArray();
        var a = Run(reqs, pool);
        var b = Run(reqs.Reverse().ToArray(), pool.Reverse().ToArray());
        Assert.Equal(a.Offers, b.Offers);
        Assert.Equal(a.Drivers.OrderBy(x => x.ListingId), b.Drivers.OrderBy(x => x.ListingId));
        Assert.Equal(a.Requests.OrderBy(x => x.RequestId), b.Requests.OrderBy(x => x.RequestId));
    }
}

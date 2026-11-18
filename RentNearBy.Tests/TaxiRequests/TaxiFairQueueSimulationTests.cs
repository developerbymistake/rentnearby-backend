using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class TaxiFairQueueSimulationTests
{
    [Theory]
    [InlineData(20261118)]
    [InlineData(7)]
    [InlineData(424242)]
    public void Invariants_hold_for_a_seeded_run(int seed)
    {
        var sim = Seeded(seed, () => new FairQueueSim(new SimConfig(seed)).Run());
        var why = $"Seed = {seed}. {sim.Stats}";
        Assert.True(sim.Stats.Served > 0 && sim.Stats.Waits > 0 && sim.Stats.Misses > 0, why);
        Assert.True(sim.Stats.Selections > 0 && sim.Stats.Confirms > 0 && sim.Stats.QuietPoints >= 10, why);
        Assert.True(sim.Stats.OverlapSkips > 0 && sim.Stats.LiveRingSkips > 0 && sim.Stats.SelectionSkips > 0, why);
        Assert.True(sim.Stats.ReturnerOffersAfterReturn > 0 && sim.Stats.JoinerOffers > 0, why);
        Assert.True(sim.Stats.AskMores > 0 && sim.Stats.Reopens > 0 && sim.Stats.HighTargetFullRings > 0, why);
    }

    [Theory]
    [InlineData(20261118)]
    [InlineData(99)]
    [InlineData(123456)]
    public void Returning_driver_never_gets_more_than_one_ahead(int seed)
    {
        var config = new SimConfig(seed) with
        {
            DriverCount = 10,
            Cooperative = true,
            Filters = false,
            OfflineFrom = 100,
            OfflineTo = 250,
            JoinAt = 180,
            QuietEvery = 0,
            CheckAhead = true
        };
        var sim = Seeded(seed, () => new FairQueueSim(config).Run());
        var why = $"Seed = {seed}. {sim.Stats}";
        Assert.True(sim.Stats.AheadChecks > 0 && sim.Stats.ReturnerOffersAfterReturn > 0 && sim.Stats.JoinerOffers > 0, why);
        Assert.True(sim.Stats.RaisedJoins == 2, why);
    }

    [Theory]
    [InlineData(20261118)]
    [InlineData(31337)]
    [InlineData(5)]
    public void No_driver_is_starved(int seed)
    {
        var config = new SimConfig(seed) with { AcceptRate = 0.15, DeclineRate = 0.35, SelectRate = 0.35, QuietEvery = 250 };
        var sim = Seeded(seed, () => new FairQueueSim(config).Run());
        Assert.True(sim.Stats.SkipChecks > 0, $"Seed = {seed}. {sim.Stats}");
        Assert.All(sim.Drivers, d => Assert.True(d.Offers > 0, $"Seed = {seed}. Driver {d.Index} ended at 0 offers"));
    }

    private static FairQueueSim Seeded(int seed, Func<FairQueueSim> run)
    {
        try
        {
            return run();
        }
        catch (Exception e)
        {
            throw new SimulationFailure($"Fair queue simulation failed. Seed = {seed}. {e.Message}", e);
        }
    }

    private sealed class SimulationFailure(string message, Exception inner) : Exception(message, inner);
}

internal sealed record SimConfig(int Seed)
{
    public int DriverCount { get; init; } = 20;
    public int RequestCount { get; init; } = 1000;
    public bool Cooperative { get; init; }
    public bool Filters { get; init; } = true;
    public int OfflineFrom { get; init; } = 200;
    public int OfflineTo { get; init; } = 400;
    public int JoinAt { get; init; } = 500;
    public int QuietEvery { get; init; } = 100;
    public bool CheckAhead { get; init; }
    public double AcceptRate { get; init; } = 0.35;
    public double DeclineRate { get; init; } = 0.2;
    public double SelectRate { get; init; } = 0.15;
}

internal sealed class SimStats
{
    public int Served, Waits, Exhausted, Misses, Selections, Confirms, QuietPoints, AheadChecks, SkipChecks;
    public int OverlapSkips, LiveRingSkips, SelectionSkips, ReturnerOffersAfterReturn, JoinerOffers, RaisedJoins;
    public int AskMores, Reopens, HighTargetFullRings;

    public override string ToString()
        => $"served={Served} waits={Waits} exhausted={Exhausted} misses={Misses} selections={Selections} confirms={Confirms} " +
           $"quiet={QuietPoints} ahead={AheadChecks} skips={SkipChecks} overlapSkips={OverlapSkips} liveRingSkips={LiveRingSkips} " +
           $"selectionSkips={SelectionSkips} returner={ReturnerOffersAfterReturn} joiner={JoinerOffers} raisedJoins={RaisedJoins} " +
           $"askMores={AskMores} reopens={Reopens} highTargetFullRings={HighTargetFullRings}";
}

internal sealed class SimDriver
{
    public required int Index;
    public required Guid Id;
    public required Guid UserId;
    public required int Seats;
    public required TaxiRoutePair[] Routes;
    public long Pass;
    public long SeenSeq;
    public DateTime? LastOffered;
    public bool Online;
    public bool EverOffline;
    public int Offers;
    public readonly List<SimOffer> OfferList = [];
    public readonly List<TaxiDateRange> Bookings = [];
    public int OffersAtJoin;
    public Dictionary<SimDriver, (int Offers, long Pass)>? Baseline;
    public long? StartPass;
    public int SkipStreak;
    public long SkipBudget;

    public bool Ringing => OfferList.Any(o => o.State == SimOfferState.Ringing);
    public bool Selected => OfferList.Any(o => o.State == SimOfferState.Selected);
}

internal enum SimOfferState { Ringing, Accepted, Declined, Missed, Selected, Confirmed, Closed, NotSelected }

internal sealed class SimOffer
{
    public required SimRequest Request;
    public required SimDriver Driver;
    public SimOfferState State = SimOfferState.Ringing;
    public DateTime ExpiresAt;
    public DateTime SelectedUntil;
}

internal sealed class SimRequest
{
    public required Guid Id;
    public required DateTime CreatedAt;
    public required int Target;
    public required int Seats;
    public required long? From;
    public required TaxiDateRange Dates;
    public required Guid Traveller;
    public required HashSet<Guid> Blocked;
    public bool Selected;
    public bool Done;
    public DateTime? WaitStart;
    public readonly List<SimOffer> Offers = [];

    public int Ringing => Offers.Count(o => o.State == SimOfferState.Ringing);
    public int Accepted => Offers.Count(o => o.State is SimOfferState.Accepted or SimOfferState.Selected);
    public bool Active => !Done && !Selected;
}

internal sealed class FairQueueSim
{
    private const long To = 1;
    private const long FromA = 2;
    private const long FromB = 3;
    private const long S = TaxiFairQueue.Stride;

    private readonly SimConfig _c;
    private readonly Random _rng;
    private readonly List<SimRequest> _requests = [];
    private readonly Guid[] _travellers;
    private DateTime _now = new(2026, 11, 18, 3, 30, 0, DateTimeKind.Utc);
    private long _citySeq;
    private long _cityLastMinPass;
    private int _created;
    private int _step;
    private int _nextQuiet;

    public readonly List<SimDriver> Drivers = [];
    public readonly SimStats Stats = new();

    public FairQueueSim(SimConfig config)
    {
        _c = config;
        _rng = new Random(config.Seed);
        _travellers = Enumerable.Range(0, 40).Select(_ => NewGuid()).ToArray();
        int[] seatChoices = [4, 6, 7];
        for (var i = 0; i < config.DriverCount; i++)
        {
            TaxiRoutePair[] routes = !config.Filters ? [new(FromA, To)]
                : (i % 3) switch { 0 => [new(FromA, To)], 1 => [new(FromB, To)], _ => [new(FromA, To), new(FromB, To)] };
            Drivers.Add(new SimDriver
            {
                Index = i,
                Id = NewGuid(),
                UserId = NewGuid(),
                Seats = config.Filters ? seatChoices[_rng.Next(3)] : 7,
                Routes = routes,
                Online = i != JoinerIndex
            });
        }
        _nextQuiet = config.QuietEvery;
    }

    private int ReturnerIndex => 0;
    private int JoinerIndex => _c.DriverCount - 1;
    private DateOnly Today => DateOnly.FromDateTime(_now);

    private Guid NewGuid()
    {
        Span<byte> b = stackalloc byte[16];
        _rng.NextBytes(b);
        return new Guid(b);
    }

    private void Check(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException($"Step {_step}, created {_created}: {what}");
    }

    public FairQueueSim Run()
    {
        while (_created < _c.RequestCount)
        {
            _step++;
            _now = _now.AddSeconds(_c.Cooperative ? 60 : NextGap());
            UpdateOnline();
            if (_c.Cooperative)
            {
                CreateCooperativeRequests();
                RunPass();
                foreach (var r in _requests.Where(r => !r.Done))
                {
                    foreach (var o in r.Offers.Where(o => o.State == SimOfferState.Ringing)) o.State = SimOfferState.Accepted;
                    if (r.Accepted >= r.Target) CloseRequest(r);
                }
                CheckState();
                continue;
            }

            ResolveRings();
            ResolveSelections();
            MakeSelections();
            RaiseTargets();
            ExpireRequests();
            foreach (var d in Drivers) d.Bookings.RemoveAll(b => b.To < Today);
            CreateRequests();
            RunPass();
            CheckState();

            if (_c.QuietEvery > 0 && _created >= _nextQuiet)
            {
                QuietPoint();
                _nextQuiet += _c.QuietEvery;
            }
        }
        QuietPoint();
        Check(Drivers.All(d => d.Online), "every driver is back online at the end");
        Check(Drivers.All(d => d.Offers > 0), "no driver ends at 0 offers");
        return this;
    }

    private int NextGap()
    {
        var p = _rng.NextDouble();
        if (p < 0.70) return _rng.Next(20, 120);
        if (p < 0.95) return _rng.Next(120, 400);
        return _rng.Next(3600, 3 * 3600);
    }

    private void UpdateOnline()
    {
        var returner = Drivers[ReturnerIndex];
        var shouldReturnerBeOnline = _created < _c.OfflineFrom || _created >= _c.OfflineTo;
        if (returner.Online && !shouldReturnerBeOnline)
        {
            returner.Online = false;
            returner.EverOffline = true;
        }
        else if (!returner.Online && shouldReturnerBeOnline) Join(returner);

        var joiner = Drivers[JoinerIndex];
        if (!joiner.Online && _created >= _c.JoinAt) Join(joiner);
    }

    private void Join(SimDriver x)
    {
        x.OffersAtJoin = x.Offers;
        x.Baseline = Drivers.Where(d => d != x && d.Online).ToDictionary(d => d, d => (d.Offers, d.Pass));
        x.StartPass = null;
        x.Online = true;
    }

    private void ResolveRings()
    {
        foreach (var o in Drivers.SelectMany(d => d.OfferList).Where(o => o.State == SimOfferState.Ringing).ToList())
        {
            if (_now >= o.ExpiresAt)
            {
                o.State = SimOfferState.Missed;
                Stats.Misses++;
                continue;
            }
            var p = _rng.NextDouble();
            if (p < _c.AcceptRate) o.State = SimOfferState.Accepted;
            else if (p < _c.AcceptRate + _c.DeclineRate) o.State = SimOfferState.Declined;
        }
    }

    private void ResolveSelections()
    {
        foreach (var o in Drivers.SelectMany(d => d.OfferList).Where(o => o.State == SimOfferState.Selected).ToList())
        {
            var p = _rng.NextDouble();
            if (_now >= o.SelectedUntil)
            {
                o.State = SimOfferState.Missed;
                o.Request.Selected = false;
            }
            else if (p < 0.4)
            {
                o.State = SimOfferState.Confirmed;
                o.Driver.Bookings.Add(o.Request.Dates);
                Stats.Confirms++;
                CloseRequest(o.Request);
            }
            else if (p < 0.55)
            {
                o.State = SimOfferState.Declined;
                o.Request.Selected = false;
            }
        }
    }

    private void MakeSelections()
    {
        foreach (var r in _requests.Where(r => r.Active && r.Accepted > 0))
        {
            var chance = r.Accepted >= r.Target ? 0.5 : _c.SelectRate;
            if (_rng.NextDouble() >= chance) continue;
            var pick = r.Offers.Where(o => o.State == SimOfferState.Accepted && !o.Driver.Selected && o.Driver.Bookings.All(b => !b.Overlaps(r.Dates))).ToList();
            if (pick.Count == 0) continue;
            var chosen = pick[_rng.Next(pick.Count)];
            chosen.State = SimOfferState.Selected;
            chosen.SelectedUntil = _now.AddSeconds(TaxiDispatchRules.ConfirmSeconds);
            r.Selected = true;
            foreach (var o in r.Offers.Where(o => o.State == SimOfferState.Ringing)) o.State = SimOfferState.Closed;
            Stats.Selections++;
        }
    }

    // Ask more (P4) and the withdraw reopen (extra 3 accepts) raise the target above the first batch,
    // so a request can sit at three live rings with more accepts still to come.
    private void RaiseTargets()
    {
        foreach (var r in _requests.Where(r => r.Active && r.Target < TaxiDispatchRules.MaxAccepts))
        {
            var p = _rng.NextDouble();
            if (r.Accepted > 0 && r.Accepted + TaxiDispatchRules.AcceptBatch > r.Target && p < 0.08)
            {
                r.Target = TaxiDispatchRules.AskMoreTarget(r.Accepted);
                Stats.AskMores++;
            }
            else if (r.Ringing > 0 && p > 0.9)
            {
                r.Target = Math.Min(TaxiDispatchRules.MaxAccepts, r.Target + TaxiDispatchRules.AcceptBatch);
                Stats.Reopens++;
            }
        }
    }

    private void ExpireRequests()
    {
        foreach (var r in _requests.Where(r => !r.Done))
        {
            if (Today > r.Dates.From) CloseRequest(r);
            else if (r.Active && r.WaitStart is { } w && (_now - w).TotalSeconds >= TaxiDispatchRules.QueueWaitSeconds && r.Ringing == 0 && r.Accepted == 0)
                CloseRequest(r);
        }
    }

    private static void CloseRequest(SimRequest r)
    {
        r.Done = true;
        r.Selected = false;
        foreach (var o in r.Offers)
        {
            o.State = o.State switch
            {
                SimOfferState.Ringing or SimOfferState.Selected => SimOfferState.Closed,
                SimOfferState.Accepted => SimOfferState.NotSelected,
                _ => o.State
            };
        }
    }

    private SimRequest NewRequest(int target, int seats, long? from, TaxiDateRange dates, Guid traveller)
    {
        var blocked = new HashSet<Guid>();
        if (_c.Filters && traveller == _travellers[7] && Drivers.Count > 5) blocked.Add(Drivers[5].UserId);
        var r = new SimRequest
        {
            Id = NewGuid(),
            CreatedAt = _now.AddTicks(_rng.Next(0, 1000)),
            Target = target,
            Seats = seats,
            From = from,
            Dates = dates,
            Traveller = traveller,
            Blocked = blocked
        };
        _requests.Add(r);
        return r;
    }

    private void CreateRequests()
    {
        var k = _rng.Next(0, 4);
        for (var i = 0; i < k && _created < _c.RequestCount; i++)
        {
            var seats = 4;
            long? from = null;
            var traveller = _travellers[_rng.Next(_travellers.Length)];
            if (_c.Filters)
            {
                var p = _rng.NextDouble();
                seats = p < 0.6 ? 4 : p < 0.85 ? 6 : 7;
                var q = _rng.NextDouble();
                from = q < 0.6 ? null : q < 0.8 ? FromA : FromB;
                if (_rng.NextDouble() < 0.03) traveller = Drivers[3].UserId;
            }
            var start = Today.AddDays(_rng.Next(0, 6));
            NewRequest(TaxiDispatchRules.AcceptBatch, seats, from, new TaxiDateRange(start, start.AddDays(_rng.Next(0, 4))), traveller);
            _created++;
        }
    }

    private void CreateCooperativeRequests()
    {
        var room = Drivers.Count(d => d.Online);
        var k = _rng.Next(0, 4);
        for (var i = 0; i < k && _created < _c.RequestCount; i++)
        {
            var target = _rng.Next(1, 4);
            if (target > room) break;
            room -= target;
            var start = Today.AddDays(1);
            NewRequest(target, 4, null, new TaxiDateRange(start, start), _travellers[_rng.Next(_travellers.Length)]);
            _created++;
        }
    }

    private bool Fits(SimRequest r, SimDriver d)
        => d.Seats >= r.Seats
           && d.UserId != r.Traveller
           && !r.Blocked.Contains(d.UserId)
           && r.Offers.All(o => o.Driver != d)
           && d.Routes.Any(x => x.ToCityKey == To && (r.From is null || x.FromCityKey == r.From))
           && d.Bookings.All(b => !b.Overlaps(r.Dates));

    private static bool Free(SimDriver d) => !d.Ringing && !d.Selected;

    private void RunPass()
    {
        var pool = Drivers.Where(d => d.Online).ToList();
        var active = _requests.Where(r => r.Active).ToList();
        var input = new PickInput(
            new FairQueueCity(_citySeq, _cityLastMinPass),
            active.Select(r => new FairQueueRequest(r.Id, r.CreatedAt, r.Target, r.Accepted, r.Ringing, r.Seats, To, r.From, r.Dates, r.Traveller,
                r.Offers.Select(o => o.Driver.Id).ToHashSet(), r.Blocked)).ToList(),
            pool.Select(d => new FairQueueDriver(d.Id, d.UserId, d.Pass, d.SeenSeq, d.LastOffered, d.Ringing, d.Selected, d.Seats, d.Routes, d.Bookings.ToList())).ToList());

        var result = TaxiFairQueue.Pick(input, _now);
        var again = TaxiFairQueue.Pick(input, _now);
        Check(result.Offers.SequenceEqual(again.Offers) && result.Drivers.SequenceEqual(again.Drivers) && result.Requests.SequenceEqual(again.Requests),
            "Pick is deterministic for the same input");

        Verify(input, result, pool, active);
        Apply(result, pool, active);
    }

    private void Verify(PickInput input, PickResult result, List<SimDriver> pool, List<SimRequest> active)
    {
        Check(result.CitySeq == _citySeq + 1, "city seq moves by one");
        Check(result.CityLastMinPass >= _cityLastMinPass, "last min pass never goes down");
        Check(result.Drivers.Count == pool.Count && pool.All(d => result.Drivers.Any(u => u.ListingId == d.Id)), "every pool driver gets an update");
        Check(result.Drivers.All(u => u.SeenSeq == _citySeq + 1), "every pool driver is seen");
        Check(result.Offers.Count == result.Offers.Select(o => o.DriverListingId).Distinct().Count(), "one ring per driver per pass");

        var byId = pool.ToDictionary(d => d.Id);
        var reqById = active.ToDictionary(r => r.Id);
        var update = result.Drivers.ToDictionary(u => u.ListingId);
        var pickIndex = result.Offers.Select((o, i) => (o.DriverListingId, i)).ToDictionary(x => x.DriverListingId, x => x.i);

        var continuing = pool.Where(d => d.SeenSeq == _citySeq).ToList();
        var floor = continuing.Count > 0 ? continuing.Min(d => d.Pass) : _cityLastMinPass;
        long PostFloor(SimDriver d) => update[d.Id].Pass - (pickIndex.ContainsKey(d.Id) ? S : 0);

        foreach (var d in pool)
        {
            var u = update[d.Id];
            Check(u.Pass >= d.Pass, $"driver {d.Index} pass never goes down");
            var expectedFloor = d.SeenSeq == _citySeq ? d.Pass : Math.Max(d.Pass, floor);
            Check(PostFloor(d) == expectedFloor, $"driver {d.Index} floor rule");
            Check(u.Picked == pickIndex.ContainsKey(d.Id), $"driver {d.Index} picked flag");
            Check(u.LastOfferedAt == (u.Picked ? _now : d.LastOffered), $"driver {d.Index} last offered");
            if (!Free(d)) Check(u.Pass == expectedFloor, $"busy driver {d.Index} keeps his place");
            if (d.Baseline is not null && d.StartPass is null)
            {
                d.StartPass = expectedFloor;
                if (expectedFloor > d.Pass) Stats.RaisedJoins++;
            }
        }

        for (var k = 0; k < result.Offers.Count; k++)
        {
            var offer = result.Offers[k];
            var r = reqById[offer.RequestId];
            var d = byId[offer.DriverListingId];
            Check(!d.Selected, $"no ring for driver {d.Index} with a pending selection");
            Check(!d.Ringing, $"no second live ring for driver {d.Index}");
            Check(Fits(r, d), $"driver {d.Index} fits the request it is rung for");
            Check(offer.DriverUserId == d.UserId, "offer carries the driver user");

            var key = (PostFloor(d), d.LastOffered, d.Id);
            foreach (var z in pool)
            {
                if (z == d || !Free(z) || !Fits(r, z)) continue;
                if (pickIndex.TryGetValue(z.Id, out var zi) && zi < k) continue;
                Check(Less(key, (PostFloor(z), z.LastOffered, z.Id)), $"driver {d.Index} was rung before a fairer driver {z.Index}");
            }
        }

        foreach (var res in result.Requests)
        {
            var r = reqById[res.RequestId];
            var need = Math.Max(0, Math.Min(TaxiDispatchRules.MaxLiveRings, r.Target - r.Accepted) - r.Ringing);
            var picked = result.Offers.Count(o => o.RequestId == r.Id);
            Check(res.Need == need && res.Picked == picked && picked <= need, "live offers of a request never exceed its need");
            var fitting = pool.Where(d => Fits(r, d)).ToList();
            if (picked < need && result.Offers.Count < input.MaxOffers)
                Check(!fitting.Any(d => Free(d) && !pickIndex.ContainsKey(d.Id)), "a short request leaves no free fitting driver unrung");
            var expected = need == 0 ? FairQueueOutcome.NoNeed
                : picked > 0 ? FairQueueOutcome.Served
                : fitting.Any(d => !Free(d) || pickIndex.ContainsKey(d.Id)) ? FairQueueOutcome.Wait
                : FairQueueOutcome.Exhausted;
            Check(res.Outcome == expected, $"outcome {res.Outcome} expected {expected}");

            if (need > 0)
            {
                foreach (var d in pool.Where(d => !pickIndex.ContainsKey(d.Id)))
                {
                    if (d.Bookings.Any(b => b.Overlaps(r.Dates))) Stats.OverlapSkips++;
                    if (d.Ringing) Stats.LiveRingSkips++;
                    if (d.Selected) Stats.SelectionSkips++;
                }
            }
        }

        TrackStarvation(result, pool, reqById, pickIndex, PostFloor);
    }

    private void TrackStarvation(PickResult result, List<SimDriver> pool, Dictionary<Guid, SimRequest> reqById,
        Dictionary<Guid, int> pickIndex, Func<SimDriver, long> postFloor)
    {
        var servedRequests = result.Offers.Select(o => reqById[o.RequestId]).Distinct().ToList();
        long Units(SimDriver x, IEnumerable<SimDriver> others)
            => others.Where(d => d != x && postFloor(d) <= postFloor(x)).Sum(d => (postFloor(x) - postFloor(d)) / S + 1);

        foreach (var x in Drivers.Where(d => !d.Online)) x.SkipStreak = 0;
        var joined = pool.Where(d => d.SeenSeq != _citySeq).ToList();
        foreach (var x in pool)
        {
            if (pickIndex.ContainsKey(x.Id))
            {
                x.SkipStreak = 0;
                continue;
            }
            if (x.SkipStreak > 0) x.SkipBudget += Units(x, joined);
            if (!Free(x) || !servedRequests.Any(r => Fits(r, x))) continue;
            if (x.SkipStreak == 0) x.SkipBudget = Units(x, pool);
            x.SkipStreak++;
            Stats.SkipChecks++;
            Check(x.SkipStreak <= x.SkipBudget, $"driver {x.Index} skipped {x.SkipStreak} times with only {x.SkipBudget} fairer turns ahead of him");
        }
    }

    private static bool Less((long Pass, DateTime? Last, Guid Id) a, (long Pass, DateTime? Last, Guid Id) b)
    {
        if (a.Pass != b.Pass) return a.Pass < b.Pass;
        if (a.Last != b.Last)
        {
            if (a.Last is null) return true;
            if (b.Last is null) return false;
            return a.Last < b.Last;
        }
        return a.Id.CompareTo(b.Id) < 0;
    }

    private void Apply(PickResult result, List<SimDriver> pool, List<SimRequest> active)
    {
        var byId = pool.ToDictionary(d => d.Id);
        foreach (var u in result.Drivers)
        {
            var d = byId[u.ListingId];
            d.Pass = u.Pass;
            d.SeenSeq = u.SeenSeq;
            d.LastOffered = u.LastOfferedAt;
        }
        _citySeq = result.CitySeq;
        _cityLastMinPass = result.CityLastMinPass;

        var reqById = active.ToDictionary(r => r.Id);
        foreach (var o in result.Offers)
        {
            var r = reqById[o.RequestId];
            var d = byId[o.DriverListingId];
            Check(r.Offers.All(x => x.Driver != d), "no (request, driver) pair twice");
            var offer = new SimOffer { Request = r, Driver = d, ExpiresAt = _now.AddSeconds(TaxiDispatchRules.RingSeconds) };
            r.Offers.Add(offer);
            d.OfferList.Add(offer);
            d.Offers++;
            if (d.Index == ReturnerIndex && d.EverOffline && _created >= _c.OfflineTo) Stats.ReturnerOffersAfterReturn++;
            if (d.Index == JoinerIndex) Stats.JoinerOffers++;
        }

        foreach (var res in result.Requests)
        {
            var r = reqById[res.RequestId];
            switch (res.Outcome)
            {
                case FairQueueOutcome.Served:
                    Stats.Served++;
                    r.WaitStart = null;
                    break;
                case FairQueueOutcome.Wait:
                    Stats.Waits++;
                    r.WaitStart ??= _now;
                    break;
                case FairQueueOutcome.Exhausted:
                    Stats.Exhausted++;
                    if (r.Ringing == 0 && r.Accepted == 0) CloseRequest(r);
                    break;
            }
        }

        if (_c.CheckAhead) CheckAhead(pool);
    }

    private void CheckAhead(List<SimDriver> pool)
    {
        foreach (var x in pool.Where(d => d.Baseline is not null))
        {
            foreach (var c in x.Baseline!.Keys.Where(c => !c.Online).ToList()) x.Baseline.Remove(c);
            // c's place at the join, counted in offers above x's starting place (x starts at the floor or his own higher pass).
            foreach (var (c, atJoin) in x.Baseline)
            {
                Stats.AheadChecks++;
                var mine = x.Offers - x.OffersAtJoin;
                var theirs = c.Offers - atJoin.Offers + (atJoin.Pass - x.StartPass!.Value) / S;
                Check(mine <= theirs + 1, $"driver {x.Index} got {mine} offers since joining, driver {c.Index} only {theirs} from the same place");
            }
        }
    }

    private void CheckState()
    {
        foreach (var d in Drivers)
        {
            Check(d.OfferList.Count(o => o.State == SimOfferState.Ringing) <= 1, $"driver {d.Index} has two live rings");
            Check(d.OfferList.Count(o => o.State == SimOfferState.Selected) <= 1, $"driver {d.Index} has two selections");
        }
        foreach (var r in _requests.Where(r => !r.Done))
        {
            Check(r.Ringing <= TaxiDispatchRules.MaxLiveRings, "at most three live rings per request");
            if (r.Target - r.Accepted > TaxiDispatchRules.MaxLiveRings && r.Ringing == TaxiDispatchRules.MaxLiveRings) Stats.HighTargetFullRings++;
            Check(r.Ringing + r.Accepted <= r.Target, "live offers never exceed the target");
            Check(r.Offers.Select(o => o.Driver).Distinct().Count() == r.Offers.Count, "no (request, driver) pair twice");
        }
    }

    // Drain every live ring and selection, then feed fresh single-need requests. Stride scheduling must
    // level every pool driver's pass in exactly the deficit, so always-available drivers end within one offer.
    private void QuietPoint()
    {
        Stats.QuietPoints++;
        foreach (var r in _requests.Where(r => !r.Done)) CloseRequest(r);
        Check(Drivers.All(Free), "everyone is free after the drain");

        Probe();
        var pool = Drivers.Where(d => d.Online).ToList();
        var max = pool.Max(d => d.Pass);
        var deficit = pool.Sum(d => (max - d.Pass) / S);
        for (var i = 0; i < deficit; i++) Probe();

        Check(pool.All(d => d.Pass == max), "passes are level after the deficit is paid");
        var steady = pool.Where(d => d.Index != ReturnerIndex && d.Index != JoinerIndex).ToList();
        Check(steady.Max(d => d.Offers) - steady.Min(d => d.Offers) <= 1, "always-available drivers differ by at most one offer at a quiet point");
    }

    private void Probe()
    {
        _now = _now.AddSeconds(1);
        var far = Today.AddDays(400);
        var r = NewRequest(1, 4, null, new TaxiDateRange(far, far), NewGuid());
        RunPass();
        Check(r.Offers.Count == 1, "a probe always gets one ring");
        r.Offers[0].State = SimOfferState.Declined;
        CloseRequest(r);
    }
}

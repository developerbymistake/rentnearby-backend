namespace RentNearBy.Core.Models.TaxiRequests;

// A driver route in city keys (MASTER_PLAN 2.2, TaxiRoutes.FromCityKey / ToCityKey).
public readonly record struct TaxiRoutePair(long FromCityKey, long ToCityKey);

public readonly record struct FairQueueCity(long Seq, long LastMinPass);

// One Open or Collecting request of the city. Ringing = its offers in Ringing now.
public sealed record FairQueueRequest(
    Guid Id,
    DateTime CreatedAt,
    int AcceptTarget,
    int AcceptedCount,
    int Ringing,
    int Seats,
    long MatchToCityKey,
    long? MatchFromCityKey,
    TaxiDateRange Dates,
    Guid TravellerUserId,
    IReadOnlySet<Guid> OfferedDriverListingIds,
    IReadOnlySet<Guid> BlockedUserIds)
{
    public int Need => TaxiFairQueue.RingsNeeded(AcceptTarget, AcceptedCount, Ringing);
}

// A static pool member of the city (section 2.5). Occupied = active bookings and legacy confirmed local taxi ranges.
public sealed record FairQueueDriver(
    Guid ListingId,
    Guid UserId,
    long Pass,
    long SeenSeq,
    DateTime? LastOfferedAt,
    bool HasLiveRing,
    bool HasPendingSelection,
    int Seats,
    IReadOnlyList<TaxiRoutePair> Routes,
    IReadOnlyList<TaxiDateRange> Occupied)
{
    public bool IsFree => !HasLiveRing && !HasPendingSelection;
}

public sealed record PickInput(
    FairQueueCity City,
    IReadOnlyList<FairQueueRequest> Requests,
    IReadOnlyList<FairQueueDriver> Pool,
    int MaxOffers = TaxiFairQueue.MaxOffersPerPass);

public enum FairQueueOutcome
{
    NoNeed,
    Served,
    Wait,
    Exhausted,
    Deferred
}

public sealed record FairQueueOffer(Guid RequestId, Guid DriverListingId, Guid DriverUserId);

public sealed record FairQueueRequestResult(Guid RequestId, int Need, int Picked, FairQueueOutcome Outcome);

public sealed record FairQueueDriverUpdate(Guid ListingId, long Pass, long SeenSeq, DateTime? LastOfferedAt, bool Picked);

public sealed record PickResult(
    IReadOnlyList<FairQueueOffer> Offers,
    IReadOnlyList<FairQueueDriverUpdate> Drivers,
    IReadOnlyList<FairQueueRequestResult> Requests,
    long CitySeq,
    long CityLastMinPass);

// Pure fair queue (MASTER_PLAN 2.6). No IO, no clock except now.
public static class TaxiFairQueue
{
    public const long Stride = TaxiDispatchRules.FairQueueStride;
    public const int MaxOffersPerPass = 200;

    // Decision 18 hook: equal weights in release 1.
    public static long WeightOf(FairQueueDriver driver) => 1;

    // Live rings (ringing + new) never pass min(3, target - accepted) (contract 12, decision 8).
    public static int RingsNeeded(int acceptTarget, int acceptedCount, int ringing)
        => Math.Clamp(Math.Min(TaxiDispatchRules.MaxLiveRings, acceptTarget - acceptedCount) - ringing, 0, TaxiDispatchRules.MaxLiveRings);

    public static bool Fits(FairQueueRequest request, FairQueueDriver driver)
    {
        if (driver.Seats < request.Seats) return false;
        if (driver.UserId == request.TravellerUserId) return false;
        if (request.BlockedUserIds.Contains(driver.UserId)) return false;
        if (request.OfferedDriverListingIds.Contains(driver.ListingId)) return false;
        if (!driver.Routes.Any(r => r.ToCityKey == request.MatchToCityKey
                && (request.MatchFromCityKey is null || r.FromCityKey == request.MatchFromCityKey))) return false;
        return !driver.Occupied.Any(o => o.Overlaps(request.Dates));
    }

    public static PickResult Pick(PickInput input, DateTime now)
    {
        var city = input.City;
        var pool = input.Pool;

        var continuing = pool.Where(d => d.SeenSeq == city.Seq).ToList();
        var floor = continuing.Count > 0 ? continuing.Min(d => d.Pass) : city.LastMinPass;

        var pass = new Dictionary<Guid, long>(pool.Count);
        foreach (var d in pool)
            pass[d.ListingId] = d.SeenSeq == city.Seq ? d.Pass : Math.Max(d.Pass, floor);

        var picked = new HashSet<Guid>();
        var offers = new List<FairQueueOffer>();
        var results = new List<FairQueueRequestResult>();

        var ordered = input.Requests.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToList();
        foreach (var request in ordered)
        {
            var need = request.Need;
            if (need == 0)
            {
                results.Add(new FairQueueRequestResult(request.Id, 0, 0, FairQueueOutcome.NoNeed));
                continue;
            }
            if (offers.Count >= input.MaxOffers)
            {
                results.Add(new FairQueueRequestResult(request.Id, need, 0, FairQueueOutcome.Deferred));
                continue;
            }

            var candidates = pool.Where(d => Fits(request, d)).ToList();
            var got = 0;
            while (got < need && offers.Count < input.MaxOffers)
            {
                FairQueueDriver? best = null;
                foreach (var d in candidates)
                {
                    if (!d.IsFree || picked.Contains(d.ListingId)) continue;
                    if (best is null || Before(d, best, pass)) best = d;
                }
                if (best is null) break;

                picked.Add(best.ListingId);
                pass[best.ListingId] += Stride / WeightOf(best);
                offers.Add(new FairQueueOffer(request.Id, best.ListingId, best.UserId));
                got++;
            }

            FairQueueOutcome outcome;
            if (got > 0) outcome = FairQueueOutcome.Served;
            else if (candidates.Any(d => !d.IsFree || picked.Contains(d.ListingId))) outcome = FairQueueOutcome.Wait;
            else outcome = FairQueueOutcome.Exhausted;
            results.Add(new FairQueueRequestResult(request.Id, need, got, outcome));
        }

        var nextSeq = city.Seq + 1;
        var updates = pool
            .Select(d => new FairQueueDriverUpdate(
                d.ListingId,
                pass[d.ListingId],
                nextSeq,
                picked.Contains(d.ListingId) ? now : d.LastOfferedAt,
                picked.Contains(d.ListingId)))
            .ToList();
        var lastMinPass = updates.Count > 0 ? Math.Max(city.LastMinPass, updates.Min(u => u.Pass)) : city.LastMinPass;

        return new PickResult(offers, updates, results, nextSeq, lastMinPass);
    }

    // Order key (Pass, LastOfferedAt nulls first, ListingId).
    private static bool Before(FairQueueDriver a, FairQueueDriver b, Dictionary<Guid, long> pass)
    {
        var c = pass[a.ListingId].CompareTo(pass[b.ListingId]);
        if (c != 0) return c < 0;
        if (a.LastOfferedAt != b.LastOfferedAt)
        {
            if (a.LastOfferedAt is null) return true;
            if (b.LastOfferedAt is null) return false;
            return a.LastOfferedAt.Value < b.LastOfferedAt.Value;
        }
        return a.ListingId.CompareTo(b.ListingId) < 0;
    }
}

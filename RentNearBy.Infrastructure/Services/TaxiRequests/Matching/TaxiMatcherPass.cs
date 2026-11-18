using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.TaxiRequests;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.Matching;

public enum MatcherPassOutcome
{
    LockBusy,
    SwitchOff,
    Nothing,
    Done,
}

public sealed record MatcherPassResult(MatcherPassOutcome Outcome, int OffersCreated = 0, bool Rerun = false);

public interface ITaxiMatcherPass
{
    Task<MatcherPassResult> RunAsync(long cityKey, CancellationToken ct);
}

// Test hook after the city lock and before commit; production uses NoMatcherBarrier.
public interface IMatcherBarrier
{
    Task ReachedAsync(long cityKey, CancellationToken ct);
}

public sealed class NoMatcherBarrier : IMatcherBarrier
{
    public Task ReachedAsync(long cityKey, CancellationToken ct) => Task.CompletedTask;
}

// A request of the city that still needs rings. Ringing = its offers ringing now.
public sealed record MatcherRequestRow(
    Guid Id, Guid TripId, Guid TravellerUserId, DateTime CreatedAt, int AcceptTarget, int AcceptedCount, int Ringing, int Seats,
    long MatchToCityKey, long? MatchFromCityKey, DateOnly DateFrom, DateOnly DateTo, DateTime? QueueWaitStartedAt, DateTime? QueueExhaustedAt);

public sealed record MatcherCityData(
    long CityKey,
    DateTime Now,
    FairQueueCity City,
    IReadOnlyList<MatcherRequestRow> Requests,
    IReadOnlyList<TaxiPoolDriver> Pool,
    IReadOnlyList<TaxiCandidatePair> Candidates,
    bool RequestLimitHit);

public sealed record MatcherApplyResult(int OffersCreated, bool Rerun, TaxiClosureResult Signals);

// One transaction of one pass. Disposing without CommitAsync rolls back.
public interface IMatcherPassUnit : IAsyncDisposable
{
    Task<bool> TryLockCityAsync(long cityKey, CancellationToken ct);
    // null = the city row is held by someone else; the pass is retried later.
    Task<MatcherCityData?> LoadAsync(long cityKey, CancellationToken ct);
    Task<MatcherApplyResult> ApplyAsync(MatcherCityData data, MatcherInput input, PickResult pick, CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
    Task AfterCommitAsync(TaxiClosureResult signals);
}

public interface IMatcherPassUnitFactory
{
    Task<IMatcherPassUnit> BeginAsync(CancellationToken ct);
}

// The fair queue input built from what the SQL of 2.5 already decided. Candidate pairs carry every eligibility rule, so a
// pool driver who is not a candidate of a request goes into that request's exclusion set and nothing is re-judged here.
// HiddenCandidates = requests with a candidate whose state row was skipped (locked): such a request is never called exhausted.
public sealed record MatcherInput(PickInput Pick, IReadOnlyDictionary<Guid, FairQueueDriver> Drivers, IReadOnlySet<Guid> HiddenCandidates)
{
    private static readonly IReadOnlySet<Guid> NoIds = new HashSet<Guid>();

    public static MatcherInput Build(MatcherCityData data)
    {
        var poolIds = data.Pool.Select(p => p.DriverListingId).ToHashSet();
        var candidatesOf = data.Candidates
            .GroupBy(c => c.RequestId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.DriverListingId).ToHashSet());

        var hidden = new HashSet<Guid>();
        var routes = new Dictionary<Guid, List<TaxiRoutePair>>();
        var requests = new List<FairQueueRequest>(data.Requests.Count);
        foreach (var r in data.Requests)
        {
            var mine = candidatesOf.GetValueOrDefault(r.Id) ?? [];
            if (mine.Any(id => !poolIds.Contains(id))) hidden.Add(r.Id);
            foreach (var id in mine.Where(poolIds.Contains))
            {
                if (!routes.TryGetValue(id, out var list)) routes[id] = list = [];
                list.Add(new TaxiRoutePair(r.MatchFromCityKey ?? 0, r.MatchToCityKey));
            }
            var excluded = poolIds.Where(id => !mine.Contains(id)).ToHashSet();
            requests.Add(new FairQueueRequest(r.Id, r.CreatedAt, r.AcceptTarget, r.AcceptedCount, r.Ringing, r.Seats,
                r.MatchToCityKey, r.MatchFromCityKey, new TaxiDateRange(r.DateFrom, r.DateTo), r.TravellerUserId, excluded, NoIds));
        }

        var drivers = data.Pool.ToDictionary(p => p.DriverListingId, p => new FairQueueDriver(
            p.DriverListingId, p.DriverUserId, p.Pass, p.SeenSeq, p.LastOfferedAt,
            HasLiveRing: !p.Free, HasPendingSelection: false, p.Seats,
            routes.TryGetValue(p.DriverListingId, out var pairs) ? pairs : [], []));

        return new MatcherInput(new PickInput(data.City, requests, drivers.Values.ToList()), drivers, hidden);
    }
}

// MASTER_PLAN B2-4: one pass for one city in one transaction. Never waits on a lock: city advisory try lock, SKIP LOCKED
// rows, driver try locks before each ring insert. A failed try lock marks the city dirty again so the holder reruns it.
public sealed class TaxiMatcherPass(
    IMatcherPassUnitFactory units,
    ITripExecutionFeatureReader feature,
    IMatcherSignal signal,
    IMatcherBarrier? barrier = null,
    TaxiRequestMetrics? metrics = null,
    ILogger<TaxiMatcherPass>? logger = null) : ITaxiMatcherPass
{
    private readonly IMatcherBarrier _barrier = barrier ?? new NoMatcherBarrier();

    public const string LockNotAvailable = "55P03";

    public Task<MatcherPassResult> RunAsync(long cityKey, CancellationToken ct)
        => PgRetry.OnceAsync(c => RunOnceAsync(cityKey, c), ct, logger);

    // The few plain writes (offer insert, request end) are bounded by MatcherSql.LockTimeoutMs; hitting it is a busy lock
    // like a failed try lock: the transaction rolls back and the city is marked dirty for a later pass.
    private async Task<MatcherPassResult> RunOnceAsync(long cityKey, CancellationToken ct)
    {
        try
        {
            return await RunUnitAsync(cityKey, ct);
        }
        catch (Exception ex) when (IsLockTimeout(ex) && !ct.IsCancellationRequested)
        {
            logger?.LogInformation("Taxi matcher pass for {CityKey} hit a held lock, skipped", cityKey);
            signal.MarkDirty(cityKey);
            return new MatcherPassResult(MatcherPassOutcome.LockBusy);
        }
    }

    internal static bool IsLockTimeout(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is PostgresException pg && pg.SqlState == LockNotAvailable) return true;
        return false;
    }

    private async Task<MatcherPassResult> RunUnitAsync(long cityKey, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        MatcherApplyResult applied;
        await using (var unit = await units.BeginAsync(ct))
        {
            if (!await unit.TryLockCityAsync(cityKey, ct))
            {
                signal.MarkDirty(cityKey);
                return new MatcherPassResult(MatcherPassOutcome.LockBusy);
            }
            if (!await feature.IsEnabledAsync()) return new MatcherPassResult(MatcherPassOutcome.SwitchOff);

            var data = await unit.LoadAsync(cityKey, ct);
            if (data is null)
            {
                signal.MarkDirty(cityKey);
                return new MatcherPassResult(MatcherPassOutcome.LockBusy);
            }
            if (data.Requests.Count == 0) return new MatcherPassResult(MatcherPassOutcome.Nothing);

            var input = MatcherInput.Build(data);
            var pick = TaxiFairQueue.Pick(input.Pick, data.Now);
            applied = await unit.ApplyAsync(data, input, pick, ct);

            await _barrier.ReachedAsync(cityKey, ct);
            await unit.CommitAsync(ct);
            await unit.AfterCommitAsync(applied.Signals);
        }
        if (applied.Rerun) signal.MarkDirty(cityKey);
        metrics?.RecordDispatch(Stopwatch.GetElapsedTime(started).TotalSeconds);
        return new MatcherPassResult(MatcherPassOutcome.Done, applied.OffersCreated, applied.Rerun);
    }
}

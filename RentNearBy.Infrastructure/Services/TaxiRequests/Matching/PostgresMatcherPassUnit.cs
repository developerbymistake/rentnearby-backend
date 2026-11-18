using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.Matching;

// Scoped: one pass per scope. Opens the READ COMMITTED transaction of the pass.
public sealed class PostgresMatcherPassUnitFactory(
    ApplicationDbContext db, TaxiRequestStore store, ITaxiPushWriter pushes, IDueActionScheduler scheduler) : IMatcherPassUnitFactory
{
    public async Task<IMatcherPassUnit> BeginAsync(CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync(MatcherSql.SetLockTimeout, ct);
        }
        catch
        {
            await tx.DisposeAsync();
            throw;
        }
        return new PostgresMatcherPassUnit(db, tx, store, pushes, scheduler);
    }
}

// Lock order of 2.4 inside the pass: city advisory lock -> city row -> request rows -> dispatch state rows -> driver ring
// locks -> offers -> due actions -> outbox rows. Every lock is a try lock or SKIP LOCKED except the offer insert and the
// request end, which are bounded by MatcherSql.LockTimeoutMs and end the pass as LockBusy.
public sealed class PostgresMatcherPassUnit(
    ApplicationDbContext db, IDbContextTransaction tx, TaxiRequestStore store, ITaxiPushWriter pushes, IDueActionScheduler scheduler) : IMatcherPassUnit
{
    public async Task<bool> TryLockCityAsync(long cityKey, CancellationToken ct)
        => await db.Database.SqlQueryRaw<bool>(MatcherSql.TryLockCity, City(cityKey)).SingleAsync(ct);

    public async Task<MatcherCityData?> LoadAsync(long cityKey, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(MatcherSql.EnsureCity, [City(cityKey)], ct);
        var cityRows = await db.Database.SqlQueryRaw<CityRow>(MatcherSql.LockCity, City(cityKey)).ToListAsync(ct);
        if (cityRows.Count == 0) return null;
        var city = new FairQueueCity(cityRows[0].Seq, cityRows[0].LastMinPass);
        var now = Utc(await db.Database.SqlQueryRaw<DateTime>(MatcherSql.Now).SingleAsync(ct));

        var rows = await db.Database.SqlQueryRaw<RequestRow>(MatcherSql.LoadRequests,
            City(cityKey), new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = MatcherSql.RequestLimit }).ToListAsync(ct);
        if (rows.Count == 0) return new MatcherCityData(cityKey, now, city, [], [], [], false);

        await TaxiCandidateQuery.EnsureDispatchStatesAsync(db, cityKey, ct);
        var pool = await TaxiCandidateQuery.LoadPoolAsync(db, cityKey, ct);
        var requests = rows.Select(r => r.ToRow()).ToList();
        var candidates = await TaxiCandidateQuery.LoadCandidatesAsync(db, requests.Select(r => r.Id).ToList(), ct);
        return new MatcherCityData(cityKey, now, city, requests, pool, candidates, rows.Count >= MatcherSql.RequestLimit);
    }

    public async Task<MatcherApplyResult> ApplyAsync(MatcherCityData data, MatcherInput input, PickResult pick, CancellationToken ct)
    {
        var rerun = data.RequestLimitHit;
        var changes = new List<TaxiRequestChange>();
        var freed = new List<Guid>();

        var wanted = pick.Offers.Select(o => o.DriverListingId).Distinct().ToArray();
        var lockedDrivers = wanted.Length == 0
            ? []
            : (await db.Database.SqlQueryRaw<Guid>(MatcherSql.TryLockDrivers, Uuids("ids", wanted)).ToListAsync(ct)).ToHashSet();

        var rung = new HashSet<Guid>();
        var rungPerRequest = new Dictionary<Guid, int>();
        foreach (var offer in pick.Offers)
        {
            if (!lockedDrivers.Contains(offer.DriverListingId))
            {
                rerun = true;
                continue;
            }
            var offerId = Guid.NewGuid();
            var inserted = await db.Database.SqlQueryRaw<InsertedRow>(MatcherSql.InsertOffer,
                Uuid("id", offerId), Uuid("requestId", offer.RequestId), Uuid("driverId", offer.DriverListingId),
                Uuid("driverUserId", offer.DriverUserId)).ToListAsync(ct);
            if (inserted.Count == 0)
            {
                rerun = true;
                continue;
            }
            var expiresAt = Utc(inserted[0].ExpiresAt);
            rung.Add(offer.DriverListingId);
            rungPerRequest[offer.RequestId] = rungPerRequest.GetValueOrDefault(offer.RequestId) + 1;
            await scheduler.AddAtAsync(db, DueActionKinds.OfferExpire, offerId, offer.RequestId, 0, expiresAt, ct);
            await pushes.RingAsync(db, new RingPush(offerId, offer.RequestId, offer.DriverUserId, expiresAt), ct);
        }

        await WriteQueueAsync(data, input, pick, rung, ct);

        var byId = data.Requests.ToDictionary(r => r.Id);
        foreach (var result in pick.Requests)
        {
            var row = byId[result.RequestId];
            switch (result.Outcome)
            {
                case FairQueueOutcome.Served:
                    var got = rungPerRequest.GetValueOrDefault(row.Id);
                    if (got < result.Picked) rerun = true;
                    if (got > 0) await UpdateAsync(MatcherSql.MarkServed, row, changes, ct);
                    break;
                case FairQueueOutcome.Wait:
                    // A wait still running has its timer; a wait that is over (its timer skipped) starts a new round.
                    if (MatcherSql.WaitRunning(row.QueueWaitStartedAt, data.Now)) break;
                    var wait = await UpdateAsync(MatcherSql.StartWait, row, changes, ct);
                    if (wait?.QueueWaitStartedAt is { } startedAt)
                        await scheduler.AddAtAsync(db, DueActionKinds.RequestWaitExpire, row.Id, row.Id, wait.WaitRound,
                            Utc(startedAt).AddSeconds(TaxiDispatchRules.QueueWaitSeconds), ct);
                    break;
                case FairQueueOutcome.Exhausted:
                    if (input.HiddenCandidates.Contains(row.Id))
                    {
                        rerun = true;
                        break;
                    }
                    // A ring still out decides it: its end frees the driver and signals the city again.
                    if (row.Ringing > 0) break;
                    var live = await store.GetLiveCountsAsync(db, row.Id, ct);
                    if (live.Ringing + live.Accepted + live.Selected == 0)
                    {
                        var end = await store.EndRequestAsync(db, row.Id, RequestStates.Expired, RequestEndReasons.AllDriversBusy, ct: ct);
                        if (end is not null)
                        {
                            changes.AddRange(end.Signals.Changes);
                            freed.AddRange(end.Signals.FreedDriverListingIds);
                        }
                    }
                    else if (row.QueueExhaustedAt is null)
                    {
                        await UpdateAsync(MatcherSql.MarkExhausted, row, changes, ct);
                    }
                    break;
                case FairQueueOutcome.Deferred:
                    rerun = true;
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        return new MatcherApplyResult(rung.Count, rerun, new TaxiClosureResult([], freed, changes));
    }

    public Task CommitAsync(CancellationToken ct) => tx.CommitAsync(ct);

    // The city itself is not in the signals: a rerun is the pass's own decision.
    public Task AfterCommitAsync(TaxiClosureResult signals) => store.AfterCommitAsync(signals);

    public ValueTask DisposeAsync() => tx.DisposeAsync();

    // Passes, SeenSeq and the city row. A pick whose ring was not inserted is not charged (its pass and last offer stay).
    private async Task WriteQueueAsync(MatcherCityData data, MatcherInput input, PickResult pick, HashSet<Guid> rung, CancellationToken ct)
    {
        if (pick.Drivers.Count > 0)
        {
            var ids = new Guid[pick.Drivers.Count];
            var passes = new long[pick.Drivers.Count];
            var seens = new long[pick.Drivers.Count];
            var lasts = new DateTime?[pick.Drivers.Count];
            for (var i = 0; i < pick.Drivers.Count; i++)
            {
                var u = pick.Drivers[i];
                var driver = input.Drivers[u.ListingId];
                var charged = !u.Picked || rung.Contains(u.ListingId);
                ids[i] = u.ListingId;
                passes[i] = charged ? u.Pass : u.Pass - TaxiFairQueue.Stride / TaxiFairQueue.WeightOf(driver);
                seens[i] = u.SeenSeq;
                lasts[i] = charged ? u.LastOfferedAt : driver.LastOfferedAt;
            }
            await db.Database.ExecuteSqlRawAsync(MatcherSql.UpdateStates,
            [
                Uuids("ids", ids),
                new NpgsqlParameter("passes", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = passes },
                new NpgsqlParameter("seens", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = seens },
                new NpgsqlParameter("lasts", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = lasts },
                City(data.CityKey),
            ], ct);
            var minPass = Math.Max(data.City.LastMinPass, passes.Min());
            await UpdateCityAsync(data.CityKey, pick.CitySeq, minPass, ct);
        }
        else
        {
            await UpdateCityAsync(data.CityKey, pick.CitySeq, pick.CityLastMinPass, ct);
        }
    }

    private Task UpdateCityAsync(long cityKey, long seq, long minPass, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(MatcherSql.UpdateCity,
        [
            new NpgsqlParameter("seq", NpgsqlDbType.Bigint) { Value = seq },
            new NpgsqlParameter("minPass", NpgsqlDbType.Bigint) { Value = minPass },
            City(cityKey),
        ], ct);

    private async Task<UpdatedRow?> UpdateAsync(string sql, MatcherRequestRow row, List<TaxiRequestChange> changes, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<UpdatedRow>(sql, Uuid("id", row.Id)).ToListAsync(ct);
        if (rows.Count == 0) return null;
        changes.Add(new TaxiRequestChange(row.Id, row.TripId, row.TravellerUserId, rows[0].Version, rows[0].Status));
        return rows[0];
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static NpgsqlParameter City(long cityKey) => new("city", NpgsqlDbType.Bigint) { Value = cityKey };

    private static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };

    private static NpgsqlParameter Uuids(string name, Guid[] values) => new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = values };

    private sealed class CityRow
    {
        public long Seq { get; set; }
        public long LastMinPass { get; set; }
    }

    private sealed class RequestRow
    {
        public Guid Id { get; set; }
        public Guid TripId { get; set; }
        public Guid TravellerUserId { get; set; }
        public DateTime CreatedAt { get; set; }
        public int AcceptTarget { get; set; }
        public int AcceptedCount { get; set; }
        public int Ringing { get; set; }
        public int Seats { get; set; }
        public long MatchToCityKey { get; set; }
        public long? MatchFromCityKey { get; set; }
        public DateOnly DateFrom { get; set; }
        public DateOnly DateTo { get; set; }
        public DateTime? QueueWaitStartedAt { get; set; }
        public DateTime? QueueExhaustedAt { get; set; }

        public MatcherRequestRow ToRow() => new(Id, TripId, TravellerUserId, CreatedAt, AcceptTarget, AcceptedCount, Ringing, Seats,
            MatchToCityKey, MatchFromCityKey, DateFrom, DateTo, QueueWaitStartedAt, QueueExhaustedAt);
    }

    private sealed class InsertedRow
    {
        public Guid OfferId { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    private sealed class UpdatedRow
    {
        public long Version { get; set; }
        public string Status { get; set; } = "";
        public int WaitRound { get; set; }
        public DateTime? QueueWaitStartedAt { get; set; }
    }
}

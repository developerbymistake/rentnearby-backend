using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Owner.Be3;

public sealed record PlanRowsEffect(Guid? ListingId, Guid? DistrictId, int HiddenBefore, int HiddenAfter, bool Changed)
{
    public static readonly PlanRowsEffect None = new(null, null, 0, 0, false);
}

public sealed record LimitsReconcileResult(int Listings, int Notified, IReadOnlyList<Guid> DistrictIds);

// Contract 3.3 / 3.4: what the plan does to rooms, photos, routes and special prices. Hidden, never deleted; nothing is hidden
// while payment is OFF (Q1). Single owner (grant, expiry) = ApplyForOwnerAsync under the caller's Users lock; the daily pass and
// the payment switch = ReconcileAsync, set-based per batch of 500 listings.
public sealed class PlanLimitsReconcile(
    ApplicationDbContext db,
    IPlanCatalogCache catalog,
    IPaymentFeatureReader payment,
    TaxiRequestClosure closure,
    IOwnerNotifier notifier,
    ILogger<PlanLimitsReconcile> logger)
{
    public const int Batch = 500;

    private readonly List<TaxiClosureResult> _afterCommit = new();
    private bool _poolChanged;

    private sealed class ListingRow
    {
        public Guid Id { get; set; }
        public Guid? DistrictId { get; set; }
    }

    private sealed class BatchRow
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public Guid? DistrictId { get; set; }
        public bool Differs { get; set; }
        public bool RoomsDiffer { get; set; }
        public bool HasLiveOffer { get; set; }
        public int HiddenBefore { get; set; }
    }

    private sealed class HiddenRow
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public int Photos { get; set; }
        public int Rooms { get; set; }
        public int Routes { get; set; }
        public int Rules { get; set; }
    }

    // ---- single owner (caller holds the transaction and the Users row lock, contract 4.1 level 2) ----

    public async Task<PlanRowsEffect> ApplyForOwnerAsync(Guid userId, string kind, PlanLimits limits, CancellationToken ct = default)
    {
        var hide = limits.PaymentEnabled;
        var today = IstTime.TodayIst(DateTime.UtcNow);
        return kind switch
        {
            ListingKinds.Stay => await ApplyStayAsync(userId, limits, hide, today, ct),
            ListingKinds.Taxi => await ApplyTaxiAsync(userId, limits, hide, ct),
            _ => PlanRowsEffect.None,
        };
    }

    private async Task<PlanRowsEffect> ApplyStayAsync(Guid userId, PlanLimits limits, bool hide, DateOnly today, CancellationToken ct)
    {
        var listing = await LiveListingAsync("\"StayListings\"", userId, ct);
        if (listing == null || !await OwnerLocks.LockStayAsync(db, listing.Id, ct)) return PlanRowsEffect.None;

        var photos = PlanRows.AsRows(await PlanRows.PhotosAsync(db, listing.Id, ct));
        var rooms = PlanRows.AsRows(await PlanRows.RoomsAsync(db, listing.Id, ct));
        var photoTargets = PlanRowChoice.ChoosePhotos(photos, limits.Photos, hide);
        var roomTargets = PlanRowChoice.ChooseIndexed(rooms, limits.RoomTypes, hide);
        var photoChanges = PlanRowChoice.Changed(photos, photoTargets);
        var roomChanges = PlanRowChoice.Changed(rooms, roomTargets);

        await PlanRows.WritePhotosAsync(db, listing.Id, photoChanges, ct);
        if (roomChanges.Count > 0) await PlanRows.WriteRoomsAsync(db, listing.Id, roomTargets, ct);
        var rules = await PlanRows.ApplySpecialPricesAsync(db, listing.Id, limits.SpecialPrices, hide, today, ct);
        if (roomChanges.Count > 0) await PlanRows.RecomputeMinPriceAsync(db, listing.Id, ct);

        var before = photos.Count(r => r.Hidden) + rooms.Count(r => r.Hidden);
        var after = PlanRowChoice.HiddenCount(photoTargets) + PlanRowChoice.HiddenCount(roomTargets);
        return new PlanRowsEffect(listing.Id, listing.DistrictId, before, after, photoChanges.Count + roomChanges.Count + rules > 0);
    }

    // Lock order 4.1: the offers closure (request rows, then the driver ring lock) runs before the listing row lock.
    private async Task<PlanRowsEffect> ApplyTaxiAsync(Guid userId, PlanLimits limits, bool hide, CancellationToken ct)
    {
        var listing = await LiveListingAsync("\"TaxiListings\"", userId, ct);
        if (listing == null) return PlanRowsEffect.None;

        var planned = await PlanRows.RoutesAsync(db, listing.Id, ct);
        var plannedTargets = PlanRowChoice.ChooseIndexed(PlanRows.AsRows(planned), limits.Routes, hide);
        var hiding = plannedTargets.Any(t => t.Hidden && planned.Any(r => r.Id == t.Id && !r.Hidden));
        if (hiding)
        {
            var visible = plannedTargets.Where(t => !t.Hidden).Select(t => planned.First(r => r.Id == t.Id)).Select(Keys).ToList();
            _afterCommit.Add(await closure.RoutesChangedAsync(db, listing.Id, visible, ct, OfferEndReasons.RoutePausedByPlan));
        }

        if (!await OwnerLocks.LockTaxiAsync(db, listing.Id, ct)) return PlanRowsEffect.None;
        var routes = PlanRows.AsRows(await PlanRows.RoutesAsync(db, listing.Id, ct));
        var targets = PlanRowChoice.ChooseIndexed(routes, limits.Routes, hide);
        var changes = PlanRowChoice.Changed(routes, targets);
        if (changes.Count > 0)
        {
            await PlanRows.WriteRoutesAsync(db, listing.Id, targets, ct);
            await PlanRows.RecomputeMinFareAsync(db, listing.Id, ct);
            _poolChanged = true;
            if (changes.Any(c => !c.Hidden && routes.Any(r => r.Id == c.Id && r.Hidden)))
                _afterCommit.Add(TaxiRequestClosure.DriverOnline(listing.Id));
        }
        return new PlanRowsEffect(listing.Id, listing.DistrictId, routes.Count(r => r.Hidden), PlanRowChoice.HiddenCount(targets), changes.Count > 0);
    }

    // After the caller's commit: matcher wake for un-hidden routes, request hints of closed offers, pickup lists. Never throws.
    public async Task AfterCommitAsync()
    {
        var results = _afterCommit.ToList();
        var pool = _poolChanged;
        _afterCommit.Clear();
        _poolChanged = false;
        if (results.Count == 0 && !pool) return;
        try
        {
            await closure.AfterCommitAsync(TaxiRequestClosure.Merge(results), pool);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "plan.rows.after_commit failed");
        }
    }

    // Contract 5.3: the PlanLimitsChanged notice goes when nothing is hidden any more (a grant brought every row back).
    public Task ClearLimitsNoticeAsync(Guid userId, CancellationToken ct = default)
        => notifier.ClearAsync(NoticeKinds.PlanLimitsChanged, NoticeKinds.NoTrip, [userId], ct);

    public void DropAfterCommit()
    {
        _afterCommit.Clear();
        _poolChanged = false;
    }

    // ---- daily pass and payment switch (set-based per batch) ----

    public async Task<LimitsReconcileResult> ReconcileAsync(string kind, CancellationToken ct = default)
    {
        if (kind is not (ListingKinds.Stay or ListingKinds.Taxi)) return new LimitsReconcileResult(0, 0, []);
        var snapshot = await catalog.GetAsync();
        var flag = await payment.GetAsync();
        var hide = flag.Enabled;
        var cte = PlanKeySql.Cte(snapshot, flag, kind);
        var today = IstTime.TodayIst(DateTime.UtcNow);

        if (hide) await ClearPlansStartingAsync(ct);

        var after = Guid.Empty;
        var listings = 0;
        var notified = 0;
        var districts = new HashSet<Guid>();
        var single = new List<BatchRow>();
        while (!ct.IsCancellationRequested) // batch-loop
        {
            var rows = await db.Database.SqlQueryRaw<BatchRow>(BatchSql(kind, cte), Uuid("after", after), Bool("hide", hide), Date("today", today)).ToListAsync(ct);
            if (rows.Count == 0) break;
            after = rows[^1].Id;

            var differing = rows.Where(r => r.Differs).ToList();
            single.AddRange(differing.Where(r => r.HasLiveOffer));
            var setBased = differing.Where(r => !r.HasLiveOffer).ToList();
            if (setBased.Count > 0)
            {
                var (changed, sent) = await PgRetry.OnceAsync(db, c => ApplyBatchAsync(kind, cte, hide, today, setBased, c), ct, logger);
                listings += changed;
                notified += sent;
                foreach (var r in setBased.Where(r => r.DistrictId != null)) districts.Add(r.DistrictId!.Value);
            }
            if (rows.Count < Batch) break;
        }

        // Rare: a taxi with a ringing or accepted offer goes through the single-owner path (closure first, lock order 4.1).
        foreach (var row in single) // batch-loop
        {
            try
            {
                if (await ApplySingleAsync(kind, row, ct)) notified++;
                listings++;
                if (row.DistrictId != null) districts.Add(row.DistrictId.Value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Plan limits reconcile failed for listing {ListingId}; retried next pass", row.Id);
            }
        }

        if (listings > 0) notifier.Wake();
        return new LimitsReconcileResult(listings, notified, districts.ToList());
    }

    private async Task<bool> ApplySingleAsync(string kind, BatchRow row, CancellationToken ct)
    {
        var flag = await payment.GetAsync();
        var snapshot = await catalog.GetAsync();
        var notified = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM \"Users\" WHERE \"Id\" = @u FOR UPDATE", [Uuid("u", row.UserId)], c);
            var limits = await LimitsAsync(snapshot, flag, row.UserId, kind, c);
            var effect = await ApplyForOwnerAsync(row.UserId, kind, limits, c);
            var sent = false;
            if (effect.HiddenAfter > effect.HiddenBefore)
            {
                var counts = await OwnerHiddenCounts.ReadAsync(db, row.UserId, kind, IstTime.TodayIst(DateTime.UtcNow), c);
                await NotifyAsync(kind, [(row.UserId, row.Id, counts.Hidden.Photos, counts.Hidden.RoomTypes, counts.Hidden.Routes)], c);
                sent = true;
            }
            else if (effect.HiddenAfter == 0)
            {
                await notifier.ClearAsync(NoticeKinds.PlanLimitsChanged, NoticeKinds.NoTrip, [row.UserId], c);
            }
            await tx.CommitAsync(c);
            return sent;
        }, ct, logger);
        await AfterCommitAsync();
        return notified;
    }

    private async Task<PlanLimits> LimitsAsync(PlanCatalogSnapshot snapshot, PaymentFeatureState flag, Guid userId, string kind, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var row = await db.UserPlans.AsNoTracking()
            .Where(u => u.UserId == userId && u.FeatureKey == kind)
            .Where(PlanRowSql.Entitled(now))
            .OrderByDescending(u => u.EndsAt)
            .Select(u => new PlanEntitlementService.ActiveRow(u.Id, u.UserId, u.PlanId, u.Source, u.StartsAt, u.EndsAt))
            .FirstOrDefaultAsync(ct);
        return PlanEntitlementService.Compute(snapshot, flag, kind, row);
    }

    // One transaction per batch: listing row locks in id order (level 5), then the row tables in the 4.1 order, then the
    // notices and pushes of owners who lost something.
    private async Task<(int Changed, int Notified)> ApplyBatchAsync(
        string kind, string cte, bool hide, DateOnly today, IReadOnlyList<BatchRow> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToArray();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var table = kind == ListingKinds.Stay ? "\"StayListings\"" : "\"TaxiListings\"";
#pragma warning disable EF1002 // table is one of two fixed names; the ids are a parameter.
        await db.Database.ExecuteSqlRawAsync(
            $"SELECT 1 FROM (SELECT \"Id\" FROM {table} WHERE \"Id\" = ANY(@ids) ORDER BY \"Id\" FOR UPDATE) x", [Ids(ids)], ct);
#pragma warning restore EF1002

        var p = new object[] { Ids(ids), Bool("hide", hide), Date("today", today) };
        if (kind == ListingKinds.Stay)
        {
            await db.Database.ExecuteSqlRawAsync(StayPhotosBatchSql(cte), Clone(p), ct);
            // Rooms are renumbered (shift, then rewrite) only for listings whose visible room count differs.
            var roomIds = rows.Where(r => r.RoomsDiffer).Select(r => r.Id).ToArray();
            if (roomIds.Length > 0)
            {
                await db.Database.ExecuteSqlRawAsync("""UPDATE "StayRooms" SET "RoomIndex" = "RoomIndex" + 1000 WHERE "StayListingId" = ANY(@ids)""", [Ids(roomIds)], ct);
                await db.Database.ExecuteSqlRawAsync(StayRoomsBatchSql(cte), [Ids(roomIds), Bool("hide", hide), Date("today", today)], ct);
            }
            await db.Database.ExecuteSqlRawAsync(StayRulesBatchSql(cte), Clone(p), ct);
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "StayListings" s SET "MinPricePerNight" =
                    (SELECT min(r."PricePerNight") FROM "StayRooms" r WHERE r."StayListingId" = s."Id" AND NOT r."HiddenByPlan")
                WHERE s."Id" = ANY(@ids)
                """, [Ids(ids)], ct);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync("""UPDATE "TaxiRoutes" SET "RouteIndex" = "RouteIndex" + 1000 WHERE "TaxiListingId" = ANY(@ids)""", [Ids(ids)], ct);
            await db.Database.ExecuteSqlRawAsync(TaxiRoutesBatchSql(cte), Clone(p), ct);
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "TaxiListings" t SET "MinFare" =
                    (SELECT min(r."Fare") FROM "TaxiRoutes" r WHERE r."TaxiListingId" = t."Id" AND NOT r."HiddenByPlan")
                WHERE t."Id" = ANY(@ids)
                """, [Ids(ids)], ct);
            _poolChanged = true;
        }

        var hidden = await db.Database.SqlQueryRaw<HiddenRow>(HiddenAfterSql(kind), Ids(ids), Date("today", today)).ToListAsync(ct);
        var before = rows.ToDictionary(r => r.Id, r => r.HiddenBefore);
        var rose = hidden.Where(h => h.Photos + h.Rooms + h.Routes + h.Rules > before.GetValueOrDefault(h.Id)).ToList();
        var cleared = hidden.Where(h => h.Photos + h.Rooms + h.Routes + h.Rules == 0).Select(h => h.UserId).Distinct().ToList();
        await NotifyAsync(kind, rose.Select(h => (h.UserId, h.Id, h.Photos, h.Rooms, h.Routes)).ToList(), ct);
        if (cleared.Count > 0) await notifier.ClearAsync(NoticeKinds.PlanLimitsChanged, NoticeKinds.NoTrip, cleared, ct);

        await tx.CommitAsync(ct);
        if (kind == ListingKinds.Taxi)
            _afterCommit.Add(new TaxiClosureResult([], ids.ToList(), []));
        await AfterCommitAsync();
        return (rows.Count, rose.Count);
    }

    // PlanLimitsChanged (contract 5.3): one stored row per owner (upserted) and one push per owner per IST day.
    private async Task NotifyAsync(string kind, IReadOnlyList<(Guid UserId, Guid ListingId, int Photos, int Rooms, int Routes)> owners, CancellationToken ct)
    {
        if (owners.Count == 0) return;
        var today = IstTime.TodayIst(DateTime.UtcNow);
        await notifier.StoreAsync(owners.Select(o => new NoticeUpsert(o.UserId, NoticeKinds.PlanLimitsChanged, NoticeKinds.NoTrip, null, o.ListingId)).ToList(), ct);
        var pushes = owners.Select(o => kind == ListingKinds.Stay
            ? StayOwnerPushCopy.PlansStarted(o.UserId, o.Photos, o.Rooms, today)
            : TaxiDriverPushCopy.PlansStarted(o.UserId, o.Routes, today)).ToList();
        await notifier.PushAsync(pushes, ct);
    }

    // PlansStarting rows go for everyone at the switch (AU-16); owners who lost something get PlanLimitsChanged instead.
    private Task<int> ClearPlansStartingAsync(CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync("""DELETE FROM "UserNotices" WHERE "TripId" = @t AND "Kind" = @k""",
            [Uuid("t", NoticeKinds.NoTrip), new NpgsqlParameter("k", NpgsqlDbType.Varchar) { Value = NoticeKinds.PlansStarting }], ct);

    private async Task<ListingRow?> LiveListingAsync(string table, Guid userId, CancellationToken ct)
    {
#pragma warning disable EF1002 // table is one of two fixed names; the user id is a parameter.
        var rows = await db.Database.SqlQueryRaw<ListingRow>(
            $"SELECT \"Id\", \"DistrictId\" FROM {table} WHERE \"UserId\" = @u AND \"IsDeleted\" = false LIMIT 1", Uuid("u", userId)).ToListAsync(ct);
#pragma warning restore EF1002
        return rows.Count == 0 ? null : rows[0];
    }

    private static RouteServeKeys Keys(PlanRows.RouteState r)
        => new(r.FromCityKey, r.ToCityKey, FromPending: r.FromCityKey == null, ToPending: r.ToCityKey == null);

    // ---- SQL (pv = PlanKeySql values with the payment rule applied; lim = the plan value per listing, or no limit while OFF) ----

    private static string Lim(string kind, string key) => $"""
        lim AS (
            SELECT l."Id", CASE WHEN @hide THEN {PlanKeySql.Column(key)} ELSE 2147483647 END AS n
            FROM {(kind == ListingKinds.Stay ? "\"StayListings\"" : "\"TaxiListings\"")} l
            {PlanRowSql.ActiveLateral("l.\"UserId\"", kind)}
            JOIN {PlanKeySql.DefaultName} ON {PlanKeySql.Join("up")}
            WHERE l."Id" = ANY(@ids))
        """;

    internal static string StayPhotosBatchSql(string cte) => $"""
        WITH {cte}, {Lim(ListingKinds.Stay, PlanFeatureKeys.Photos)},
        ranked AS (
            SELECT p."Id", row_number() OVER (PARTITION BY p."StayListingId" ORDER BY p."HiddenByPlan", p."PhotoOrder", p."Id") AS rn, lim.n
            FROM "StayPhotos" p JOIN lim ON lim."Id" = p."StayListingId")
        UPDATE "StayPhotos" x SET "HiddenByPlan" = (r.rn > r.n)
        FROM ranked r WHERE x."Id" = r."Id" AND x."HiddenByPlan" <> (r.rn > r.n)
        """;

    internal static string StayRoomsBatchSql(string cte) => $"""
        WITH {cte}, {Lim(ListingKinds.Stay, PlanFeatureKeys.RoomTypes)},
        ranked AS (
            SELECT r."Id", row_number() OVER (PARTITION BY r."StayListingId" ORDER BY r."HiddenByPlan", r."RoomIndex", r."Id") AS rn, lim.n
            FROM "StayRooms" r JOIN lim ON lim."Id" = r."StayListingId")
        UPDATE "StayRooms" x SET "HiddenByPlan" = (r.rn > r.n),
            "RoomIndex" = (CASE WHEN r.rn <= r.n THEN r.rn ELSE {PlanRowBands.HiddenBase} + r.rn - r.n END)::smallint
        FROM ranked r WHERE x."Id" = r."Id"
        """;

    internal static string StayRulesBatchSql(string cte) => $"""
        WITH {cte}, {Lim(ListingKinds.Stay, PlanFeatureKeys.SpecialPrices)},
        ranked AS (
            SELECT sp."Id", row_number() OVER (PARTITION BY sp."StayListingId" ORDER BY sp."PausedByPlan", sp."DateTo", sp."Id") AS rn, lim.n
            FROM "StaySpecialPrices" sp JOIN lim ON lim."Id" = sp."StayListingId" WHERE sp."DateTo" >= @today)
        UPDATE "StaySpecialPrices" x SET "PausedByPlan" = (r.rn > r.n), "Version" = x."Version" + 1, "UpdatedAt" = now()
        FROM ranked r WHERE x."Id" = r."Id" AND x."PausedByPlan" <> (r.rn > r.n)
        """;

    internal static string TaxiRoutesBatchSql(string cte) => $"""
        WITH {cte}, {Lim(ListingKinds.Taxi, PlanFeatureKeys.Routes)},
        ranked AS (
            SELECT r."Id", row_number() OVER (PARTITION BY r."TaxiListingId" ORDER BY r."HiddenByPlan", r."RouteIndex", r."Id") AS rn, lim.n
            FROM "TaxiRoutes" r JOIN lim ON lim."Id" = r."TaxiListingId")
        UPDATE "TaxiRoutes" x SET "HiddenByPlan" = (r.rn > r.n),
            "RouteIndex" = (CASE WHEN r.rn <= r.n THEN r.rn ELSE {PlanRowBands.HiddenBase} + r.rn - r.n END)::smallint
        FROM ranked r WHERE x."Id" = r."Id"
        """;

    // One page of 500 listings by Id with "differs" = visible counts not min(limit, rows), a hidden row while nothing may be
    // hidden, or plan-paused rules that differ; HiddenBefore feeds the "lost something" test.
    internal static string BatchSql(string kind, string cte)
    {
        var stay = kind == ListingKinds.Stay;
        var table = stay ? "\"StayListings\"" : "\"TaxiListings\"";
        var counts = stay
            ? """
              pc.photos, pc.photos_vis, rc.rooms, rc.rooms_vis, sc.rules, sc.rules_live,
              0::bigint AS routes, 0::bigint AS routes_vis, false AS live_offer
              """
            : $"""
              0::bigint AS photos, 0::bigint AS photos_vis, 0::bigint AS rooms, 0::bigint AS rooms_vis, 0::bigint AS rules, 0::bigint AS rules_live,
              tc.routes, tc.routes_vis,
              (EXISTS (SELECT 1 FROM "TaxiRequestOffers" o WHERE o."DriverListingId" = l."Id" AND o."Status" = '{OfferStates.Ringing}')
               OR EXISTS (SELECT 1 FROM "TaxiRequestOffers" o WHERE o."DriverListingId" = l."Id" AND o."Status" = '{OfferStates.Accepted}')) AS live_offer
              """;
        // One pass per child table: the total and the visible count together (count FILTER).
        var countJoins = stay
            ? """
              LEFT JOIN LATERAL (SELECT count(*) AS photos, count(*) FILTER (WHERE NOT p."HiddenByPlan") AS photos_vis
                                 FROM "StayPhotos" p WHERE p."StayListingId" = l."Id") pc ON true
              LEFT JOIN LATERAL (SELECT count(*) AS rooms, count(*) FILTER (WHERE NOT r."HiddenByPlan") AS rooms_vis
                                 FROM "StayRooms" r WHERE r."StayListingId" = l."Id") rc ON true
              LEFT JOIN LATERAL (SELECT count(*) AS rules, count(*) FILTER (WHERE NOT sp."PausedByPlan") AS rules_live
                                 FROM "StaySpecialPrices" sp WHERE sp."StayListingId" = l."Id" AND sp."DateTo" >= @today) sc ON true
              """
            : """
              LEFT JOIN LATERAL (SELECT count(*) AS routes, count(*) FILTER (WHERE NOT r."HiddenByPlan") AS routes_vis
                                 FROM "TaxiRoutes" r WHERE r."TaxiListingId" = l."Id") tc ON true
              """;
        var photos = stay ? $"CASE WHEN @hide THEN {PlanKeySql.Column(PlanFeatureKeys.Photos)} ELSE 2147483647 END" : "0";
        var rooms = stay ? $"CASE WHEN @hide THEN {PlanKeySql.Column(PlanFeatureKeys.RoomTypes)} ELSE 2147483647 END" : "0";
        var rules = stay ? $"CASE WHEN @hide THEN {PlanKeySql.Column(PlanFeatureKeys.SpecialPrices)} ELSE 2147483647 END" : "0";
        var routes = stay ? "0" : $"CASE WHEN @hide THEN {PlanKeySql.Column(PlanFeatureKeys.Routes)} ELSE 2147483647 END";
        return $"""
            WITH {cte},
            page AS (
                SELECT l."Id", l."UserId", l."DistrictId", {PlanKeySql.DefaultName}.*,
                       {counts}
                FROM {table} l
                {PlanRowSql.ActiveLateral("l.\"UserId\"", kind)}
                JOIN {PlanKeySql.DefaultName} ON {PlanKeySql.Join("up")}
                {countJoins}
                WHERE l."IsDeleted" = false AND l."Id" > @after
                ORDER BY l."Id"
                LIMIT {Batch})
            SELECT "Id", "UserId", "DistrictId",
                   (photos_vis <> LEAST(photos, {Strip(photos)}) OR rooms_vis <> LEAST(rooms, {Strip(rooms)})
                    OR rules_live <> LEAST(rules, {Strip(rules)}) OR routes_vis <> LEAST(routes, {Strip(routes)})) AS "Differs",
                   (rooms_vis <> LEAST(rooms, {Strip(rooms)})) AS "RoomsDiffer",
                   live_offer AS "HasLiveOffer",
                   ((photos - photos_vis) + (rooms - rooms_vis) + (rules - rules_live) + (routes - routes_vis))::int AS "HiddenBefore"
            FROM page ORDER BY "Id"
            """;
    }

    // The page CTE selects pv.* so the plan columns are reachable unqualified.
    private static string Strip(string expr) => expr.Replace($"{PlanKeySql.DefaultName}.", string.Empty, StringComparison.Ordinal);

    private static string HiddenAfterSql(string kind) => kind == ListingKinds.Stay
        ? """
          SELECT l."Id", l."UserId",
            (SELECT count(*) FROM "StayPhotos" p WHERE p."StayListingId" = l."Id" AND p."HiddenByPlan")::int AS "Photos",
            (SELECT count(*) FROM "StayRooms" r WHERE r."StayListingId" = l."Id" AND r."HiddenByPlan")::int AS "Rooms",
            0 AS "Routes",
            (SELECT count(*) FROM "StaySpecialPrices" sp WHERE sp."StayListingId" = l."Id" AND sp."PausedByPlan" AND sp."DateTo" >= @today)::int AS "Rules"
          FROM "StayListings" l WHERE l."Id" = ANY(@ids)
          """
        : """
          SELECT l."Id", l."UserId", 0 AS "Photos", 0 AS "Rooms",
            (SELECT count(*) FROM "TaxiRoutes" r WHERE r."TaxiListingId" = l."Id" AND r."HiddenByPlan")::int AS "Routes",
            0 AS "Rules"
          FROM "TaxiListings" l WHERE l."Id" = ANY(@ids)
          """;

    private static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };

    private static NpgsqlParameter Bool(string name, bool value) => new(name, NpgsqlDbType.Boolean) { Value = value };

    private static NpgsqlParameter Date(string name, DateOnly value) => new(name, NpgsqlDbType.Date) { Value = value };

    private static NpgsqlParameter Ids(Guid[] ids) => new("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids };

    private static object[] Clone(object[] parameters)
        => parameters.Cast<NpgsqlParameter>().Select(p => (object)p.Clone()).ToArray();
}

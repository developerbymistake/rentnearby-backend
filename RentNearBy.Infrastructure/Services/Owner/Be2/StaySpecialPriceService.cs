using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Owner.Be2;

// P1..P6 (contract 2.3, 4.1..4.3). Every write: owner context, then the stay row FOR UPDATE (level 5, its own statement so the
// checks after it read committed rows), then the special price rows, then the notice rows; one retry on 40P01 / 40001.
// The daily counter is read under the stay lock and written in the same statement as the change; replays and no-change saves
// never count. Cache bumps run in the handler after commit.
public sealed class StaySpecialPriceService(
    ApplicationDbContext db, OwnerContextReader contexts, FestivalCache festivals, IOwnerNotifier notifier, ILogger<StaySpecialPriceService> logger)
{
    public const string NotFoundMessage = "Special price not found";

    public sealed class SpecialPriceRow
    {
        public Guid Id { get; set; }
        public Guid StayListingId { get; set; }
        public DateOnly DateFrom { get; set; }
        public DateOnly DateTo { get; set; }
        public string Direction { get; set; } = string.Empty;
        public short Percent { get; set; }
        public Guid? FestivalDateId { get; set; }
        public string? LabelCopy { get; set; }
        public bool IsPaused { get; set; }
        public bool PausedByPlan { get; set; }
        public int Version { get; set; }
        public int Active { get; set; }
    }

    private sealed class SpecialPriceLockRow
    {
        public DateOnly? SavesDay { get; set; }
        public short SavesCount { get; set; }
        public Guid? RuleId { get; set; }
        public Guid? RuleStayId { get; set; }
        public DateOnly? RuleFrom { get; set; }
        public DateOnly? RuleTo { get; set; }
        public string? RuleDirection { get; set; }
        public short? RulePercent { get; set; }
        public Guid? RuleFestivalId { get; set; }
        public string? RuleLabel { get; set; }
        public bool? RulePaused { get; set; }
        public bool? RulePausedByPlan { get; set; }
        public int? RuleVersion { get; set; }

        public SpecialPriceRow? Rule => RuleId is not { } id ? null : new SpecialPriceRow
        {
            Id = id, StayListingId = RuleStayId!.Value, DateFrom = RuleFrom!.Value, DateTo = RuleTo!.Value, Direction = RuleDirection!,
            Percent = RulePercent!.Value, FestivalDateId = RuleFestivalId, LabelCopy = RuleLabel, IsPaused = RulePaused!.Value,
            PausedByPlan = RulePausedByPlan!.Value, Version = RuleVersion!.Value,
        };
    }

    private sealed class SpecialPriceCheckRow
    {
        public int Active { get; set; }
        public Guid? OverlapId { get; set; }
        public string? OverlapLabel { get; set; }
        public DateOnly? OverlapFrom { get; set; }
        public DateOnly? OverlapTo { get; set; }
    }

    // ---------- P1 ----------

    // ctx + the active rules + the visible rooms (query filter): 3 statements.
    public async Task<Be2Result<SpecialPriceListDto>> ListAsync(Guid me, CancellationToken ct = default)
    {
        var ctx = await contexts.LoadAsync(me, ListingKinds.Stay, ct: ct);
        if (ctx == null) return Be2Result<SpecialPriceListDto>.Fail(OwnerErrorTypes.ListingNotFound);
        var rows = await db.Set<StaySpecialPrice>().AsNoTracking()
            .Where(p => p.StayListingId == ctx.ListingId && p.DateTo >= ctx.Today)
            .OrderBy(p => p.DateFrom).ThenBy(p => p.Id)
            .ToListAsync(ct);
        var rooms = await db.StayRooms.AsNoTracking()
            .Where(r => r.StayListingId == ctx.ListingId)
            .OrderBy(r => r.RoomIndex)
            .Select(r => new SpecialPriceRoomDto(r.Id, r.RoomName, r.PricePerNight))
            .ToListAsync(ct);
        return new(new SpecialPriceListDto(
            rows.Select(ToItem).ToList(), Meters(rows.Count, ctx.Limits.SpecialPrices),
            StaySpecialPriceRules.SavesLeft(ctx.SpecialPriceSavesDay, ctx.SpecialPriceSavesCount, ctx.Today),
            rooms, ctx.Limits.PaymentEnabled, ctx.StateName));
    }

    // ---------- P2 ----------

    public async Task<Be2Result<SpecialPriceWriteDto>> CreateAsync(Guid me, CreateSpecialPriceRequest request, CancellationToken ct = default)
    {
        var ctx = await contexts.LoadAsync(me, ListingKinds.Stay, ct: ct);
        if (ctx == null) return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.ListingNotFound);
        var stateFestivals = await festivals.GetAsync(db, ctx.StateName, ctx.Today, ct);
        var festival = request.FestivalDateId is { } fid ? stateFestivals.FirstOrDefault(f => f.Id == fid) : null;
        var label = StaySpecialPriceRules.Label(festival?.Name);

        return await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var lockRow = await LockAsync(ctx.ListingId, null, c);
            if (lockRow == null) return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.ListingNotFound);
            var savesLeft = StaySpecialPriceRules.SavesLeft(lockRow.SavesDay, lockRow.SavesCount, ctx.Today);

            var own = await FindAsync(request.Id, c);
            var check = await CheckAsync(ctx.ListingId, ctx.Today, request.Id, request.DateFrom, request.DateTo, c);
            if (own != null)
            {
                await tx.CommitAsync(c);
                return own.StayListingId == ctx.ListingId
                    ? new Be2Result<SpecialPriceWriteDto>(new SpecialPriceWriteDto(ToItem(own), Meters(check.Active, ctx.Limits.SpecialPrices), savesLeft), Replay: true)
                    : Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.IdTaken);
            }

            if (StaySpecialPriceRules.Invalid(request.Direction, request.Percent, request.DateFrom, request.DateTo, ctx.Today) is { } invalid)
                return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.SpecialPriceInvalid, invalid);
            if (StaySpecialPriceRules.SavesLimitReached(lockRow.SavesDay, lockRow.SavesCount, ctx.Today))
                return SavesRefusal<SpecialPriceWriteDto>(lockRow, ctx.Today);
            if (StaySpecialPriceRules.LimitReached(check.Active, ctx.Limits.SpecialPrices))
                return LimitRefusal<SpecialPriceWriteDto>(check.Active, ctx);
            if (check.OverlapId is { } overlapId)
                return Overlap<SpecialPriceWriteDto>(overlapId, check);

            var inserted = await db.Database.SqlQuery<SpecialPriceRow>($"""
                WITH ins AS (
                    INSERT INTO "StaySpecialPrices" ("Id", "StayListingId", "DateFrom", "DateTo", "Direction", "Percent", "FestivalDateId",
                                                     "LabelCopy", "IsPaused", "PausedByPlan", "Version", "CreatedAt", "UpdatedAt")
                    VALUES ({request.Id}, {ctx.ListingId}, {request.DateFrom}, {request.DateTo}, {request.Direction}, {(short)request.Percent},
                            {festival?.Id}, {label}, false, false, 1, now(), now())
                    ON CONFLICT ("Id") DO NOTHING
                    RETURNING *
                ), cnt AS (
                    UPDATE "StayListings"
                    SET "SpecialPriceSavesCount" = CASE WHEN "SpecialPriceSavesDay" = {ctx.Today} THEN "SpecialPriceSavesCount" + 1 ELSE 1 END,
                        "SpecialPriceSavesDay" = {ctx.Today}
                    WHERE "Id" = {ctx.ListingId} AND EXISTS (SELECT 1 FROM ins)
                )
                SELECT i."Id", i."StayListingId", i."DateFrom", i."DateTo", i."Direction", i."Percent", i."FestivalDateId", i."LabelCopy",
                       i."IsPaused", i."PausedByPlan", i."Version", {check.Active + 1} AS "Active"
                FROM ins i
                """).ToListAsync(c);
            if (inserted.Count == 0) return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.IdTaken);

            await ClearFestivalNoticesAsync(me, request.DateFrom, request.DateTo, stateFestivals, c);
            await tx.CommitAsync(c);
            return new Be2Result<SpecialPriceWriteDto>(
                new SpecialPriceWriteDto(ToItem(inserted[0]), Meters(check.Active + 1, ctx.Limits.SpecialPrices), Math.Max(0, savesLeft - 1)),
                Created: true, DistrictId: ctx.DistrictId, Changed: true);
        }, ct, logger);
    }

    // ---------- P3 ----------

    public async Task<Be2Result<SpecialPriceWriteDto>> UpdateAsync(Guid me, Guid id, UpdateSpecialPriceRequest request, CancellationToken ct = default)
    {
        var ctx = await contexts.LoadAsync(me, ListingKinds.Stay, ct: ct);
        if (ctx == null) return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.ListingNotFound);
        var stateFestivals = await festivals.GetAsync(db, ctx.StateName, ctx.Today, ct);
        var festival = request.FestivalDateId is { } fid ? stateFestivals.FirstOrDefault(f => f.Id == fid) : null;
        var label = StaySpecialPriceRules.Label(festival?.Name);

        return await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var lockRow = await LockAsync(ctx.ListingId, null, c);
            if (lockRow == null) return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.ListingNotFound);
            var savesLeft = StaySpecialPriceRules.SavesLeft(lockRow.SavesDay, lockRow.SavesCount, ctx.Today);
            var row = await FindAsync(id, c);
            if (row == null || row.StayListingId != ctx.ListingId || !StaySpecialPriceRules.IsActive(row.DateTo, ctx.Today))
                return Be2Result<SpecialPriceWriteDto>.Missing(NotFoundMessage);
            var check = await CheckAsync(ctx.ListingId, ctx.Today, id, request.DateFrom, request.DateTo, c);
            var meters = Meters(check.Active, ctx.Limits.SpecialPrices);

            var same = StaySpecialPriceRules.SameRule(request.DateFrom, request.DateTo, request.Direction, request.Percent,
                row.DateFrom, row.DateTo, row.Direction, row.Percent) && (festival == null || festival.Id == row.FestivalDateId);
            if (row.Version != request.Version)
                return same
                    ? new Be2Result<SpecialPriceWriteDto>(new SpecialPriceWriteDto(ToItem(row), meters, savesLeft), Replay: true)
                    : Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.StaleEdit, new StaleEditDto<SpecialPriceItemDto>(ToItem(row)));
            if (same) return new(new SpecialPriceWriteDto(ToItem(row), meters, savesLeft), Replay: true);

            if (StaySpecialPriceRules.Invalid(request.Direction, request.Percent, request.DateFrom, request.DateTo, ctx.Today, row.DateFrom) is { } invalid)
                return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.SpecialPriceInvalid, invalid);
            if (StaySpecialPriceRules.SavesLimitReached(lockRow.SavesDay, lockRow.SavesCount, ctx.Today))
                return SavesRefusal<SpecialPriceWriteDto>(lockRow, ctx.Today);
            if (check.OverlapId is { } overlapId)
                return Overlap<SpecialPriceWriteDto>(overlapId, check);

            var updated = await db.Database.SqlQuery<SpecialPriceRow>($"""
                WITH upd AS (
                    UPDATE "StaySpecialPrices"
                    SET "DateFrom" = {request.DateFrom}, "DateTo" = {request.DateTo}, "Direction" = {request.Direction},
                        "Percent" = {(short)request.Percent},
                        "FestivalDateId" = COALESCE({festival?.Id}, "FestivalDateId"), "LabelCopy" = COALESCE({label}, "LabelCopy"),
                        "Version" = "Version" + 1, "UpdatedAt" = now()
                    WHERE "Id" = {id} AND "StayListingId" = {ctx.ListingId} AND "Version" = {request.Version}
                    RETURNING *
                ), cnt AS (
                    UPDATE "StayListings"
                    SET "SpecialPriceSavesCount" = CASE WHEN "SpecialPriceSavesDay" = {ctx.Today} THEN "SpecialPriceSavesCount" + 1 ELSE 1 END,
                        "SpecialPriceSavesDay" = {ctx.Today}
                    WHERE "Id" = {ctx.ListingId} AND EXISTS (SELECT 1 FROM upd)
                )
                SELECT u."Id", u."StayListingId", u."DateFrom", u."DateTo", u."Direction", u."Percent", u."FestivalDateId", u."LabelCopy",
                       u."IsPaused", u."PausedByPlan", u."Version", {check.Active} AS "Active"
                FROM upd u
                """).ToListAsync(c);
            if (updated.Count == 0) return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.StaleEdit, new StaleEditDto<SpecialPriceItemDto>(ToItem(row)));

            await ClearFestivalNoticesAsync(me, request.DateFrom, request.DateTo, stateFestivals, c);
            await tx.CommitAsync(c);
            return new Be2Result<SpecialPriceWriteDto>(
                new SpecialPriceWriteDto(ToItem(updated[0]), meters, Math.Max(0, savesLeft - 1)), DistrictId: ctx.DistrictId, Changed: true);
        }, ct, logger);
    }

    // ---------- P4 ----------

    // ctx + lock + (delete and counter in one statement). A row that is gone is a success with no counter.
    public async Task<Be2Result<bool>> DeleteAsync(Guid me, Guid id, CancellationToken ct = default)
    {
        var ctx = await contexts.LoadAsync(me, ListingKinds.Stay, ct: ct);
        if (ctx == null) return Be2Result<bool>.Fail(OwnerErrorTypes.ListingNotFound);

        return await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var lockRow = await LockAsync(ctx.ListingId, null, c);
            if (lockRow == null) return Be2Result<bool>.Fail(OwnerErrorTypes.ListingNotFound);
            if (StaySpecialPriceRules.SavesLimitReached(lockRow.SavesDay, lockRow.SavesCount, ctx.Today))
            {
                var exists = await db.Set<StaySpecialPrice>().AnyAsync(p => p.Id == id && p.StayListingId == ctx.ListingId, c);
                return exists ? SavesRefusal<bool>(lockRow, ctx.Today) : new Be2Result<bool>(true, Replay: true);
            }

            var deleted = await db.Database.SqlQuery<Guid>($"""
                WITH del AS (
                    DELETE FROM "StaySpecialPrices" WHERE "Id" = {id} AND "StayListingId" = {ctx.ListingId} RETURNING "Id"
                ), cnt AS (
                    UPDATE "StayListings"
                    SET "SpecialPriceSavesCount" = CASE WHEN "SpecialPriceSavesDay" = {ctx.Today} THEN "SpecialPriceSavesCount" + 1 ELSE 1 END,
                        "SpecialPriceSavesDay" = {ctx.Today}
                    WHERE "Id" = {ctx.ListingId} AND EXISTS (SELECT 1 FROM del)
                )
                SELECT "Id" AS "Value" FROM del
                """).ToListAsync(c);
            await tx.CommitAsync(c);
            return new Be2Result<bool>(true, Replay: deleted.Count == 0, DistrictId: ctx.DistrictId, Changed: deleted.Count > 0);
        }, ct, logger);
    }

    // ---------- P5 ----------

    public async Task<Be2Result<SpecialPriceWriteDto>> PauseAsync(Guid me, Guid id, PauseSpecialPriceRequest request, CancellationToken ct = default)
    {
        var ctx = await contexts.LoadAsync(me, ListingKinds.Stay, ct: ct);
        if (ctx == null) return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.ListingNotFound);

        return await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var lockRow = await LockAsync(ctx.ListingId, id, c);
            if (lockRow == null) return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.ListingNotFound);
            var savesLeft = StaySpecialPriceRules.SavesLeft(lockRow.SavesDay, lockRow.SavesCount, ctx.Today);
            if (lockRow.Rule is not { } row) return Be2Result<SpecialPriceWriteDto>.Missing(NotFoundMessage);

            if (row.Version != request.Version || row.IsPaused == request.Paused)
            {
                if (row.IsPaused != request.Paused)
                    return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.StaleEdit, new StaleEditDto<SpecialPriceItemDto>(ToItem(row)));
                var active = await ActiveCountAsync(ctx.ListingId, ctx.Today, c);
                return new(new SpecialPriceWriteDto(ToItem(row), Meters(active, ctx.Limits.SpecialPrices), savesLeft), Replay: true);
            }
            if (StaySpecialPriceRules.SavesLimitReached(lockRow.SavesDay, lockRow.SavesCount, ctx.Today))
                return SavesRefusal<SpecialPriceWriteDto>(lockRow, ctx.Today);

            var updated = await db.Database.SqlQuery<SpecialPriceRow>($"""
                WITH upd AS (
                    UPDATE "StaySpecialPrices" SET "IsPaused" = {request.Paused}, "Version" = "Version" + 1, "UpdatedAt" = now()
                    WHERE "Id" = {id} AND "StayListingId" = {ctx.ListingId} AND "Version" = {request.Version}
                    RETURNING *
                ), cnt AS (
                    UPDATE "StayListings"
                    SET "SpecialPriceSavesCount" = CASE WHEN "SpecialPriceSavesDay" = {ctx.Today} THEN "SpecialPriceSavesCount" + 1 ELSE 1 END,
                        "SpecialPriceSavesDay" = {ctx.Today}
                    WHERE "Id" = {ctx.ListingId} AND EXISTS (SELECT 1 FROM upd)
                )
                SELECT u."Id", u."StayListingId", u."DateFrom", u."DateTo", u."Direction", u."Percent", u."FestivalDateId", u."LabelCopy",
                       u."IsPaused", u."PausedByPlan", u."Version",
                       (SELECT count(*)::int FROM "StaySpecialPrices" a WHERE a."StayListingId" = {ctx.ListingId} AND a."DateTo" >= {ctx.Today}) AS "Active"
                FROM upd u
                """).ToListAsync(c);
            if (updated.Count == 0) return await LostRaceAsync(id, ctx, savesLeft, current => current.IsPaused == request.Paused, c);
            await tx.CommitAsync(c);
            return new Be2Result<SpecialPriceWriteDto>(
                new SpecialPriceWriteDto(ToItem(updated[0]), Meters(updated[0].Active, ctx.Limits.SpecialPrices), Math.Max(0, savesLeft - 1)),
                DistrictId: ctx.DistrictId, Changed: true);
        }, ct, logger);
    }

    // ---------- P6 ----------

    // "Make this one live" while rules are paused by the plan: the chosen rule ranks first, then the rules live now (DateTo, Id),
    // then the rest; rank <= SpecialPrices is live. With the Free limit 1 this is PausedByPlan = (Id <> @id) (contract 2.3).
    public async Task<Be2Result<SpecialPriceWriteDto>> MakeLiveAsync(Guid me, Guid id, LiveSpecialPriceRequest request, CancellationToken ct = default)
    {
        var ctx = await contexts.LoadAsync(me, ListingKinds.Stay, ct: ct);
        if (ctx == null) return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.ListingNotFound);
        var limit = Math.Max(0, ctx.Limits.SpecialPrices);

        return await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var lockRow = await LockAsync(ctx.ListingId, id, c);
            if (lockRow == null) return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.ListingNotFound);
            var savesLeft = StaySpecialPriceRules.SavesLeft(lockRow.SavesDay, lockRow.SavesCount, ctx.Today);
            if (lockRow.Rule is not { } row || row.DateTo < ctx.Today) return Be2Result<SpecialPriceWriteDto>.Missing(NotFoundMessage);

            if (row.Version != request.Version || !row.PausedByPlan)
            {
                if (row.PausedByPlan)
                    return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.StaleEdit, new StaleEditDto<SpecialPriceItemDto>(ToItem(row)));
                var active = await ActiveCountAsync(ctx.ListingId, ctx.Today, c);
                return new(new SpecialPriceWriteDto(ToItem(row), Meters(active, ctx.Limits.SpecialPrices), savesLeft), Replay: true);
            }
            if (limit == 0) return LimitRefusal<SpecialPriceWriteDto>(await ActiveCountAsync(ctx.ListingId, ctx.Today, c), ctx);
            if (StaySpecialPriceRules.SavesLimitReached(lockRow.SavesDay, lockRow.SavesCount, ctx.Today))
                return SavesRefusal<SpecialPriceWriteDto>(lockRow, ctx.Today);

            var updated = await db.Database.SqlQuery<SpecialPriceRow>($"""
                WITH ranked AS (
                    SELECT p."Id", row_number() OVER (ORDER BY (p."Id" = {id}) DESC, p."PausedByPlan", p."DateTo", p."Id") AS rk
                    FROM "StaySpecialPrices" p
                    WHERE p."StayListingId" = {ctx.ListingId} AND p."DateTo" >= {ctx.Today}
                ), upd AS (
                    UPDATE "StaySpecialPrices" t
                    SET "PausedByPlan" = (r.rk > {limit}), "Version" = t."Version" + 1, "UpdatedAt" = now()
                    FROM ranked r
                    WHERE t."Id" = r."Id" AND t."PausedByPlan" <> (r.rk > {limit})
                      AND EXISTS (SELECT 1 FROM "StaySpecialPrices" v WHERE v."Id" = {id} AND v."Version" = {request.Version} AND v."PausedByPlan")
                    RETURNING t.*
                ), cnt AS (
                    UPDATE "StayListings"
                    SET "SpecialPriceSavesCount" = CASE WHEN "SpecialPriceSavesDay" = {ctx.Today} THEN "SpecialPriceSavesCount" + 1 ELSE 1 END,
                        "SpecialPriceSavesDay" = {ctx.Today}
                    WHERE "Id" = {ctx.ListingId} AND EXISTS (SELECT 1 FROM upd WHERE upd."Id" = {id})
                )
                SELECT u."Id", u."StayListingId", u."DateFrom", u."DateTo", u."Direction", u."Percent", u."FestivalDateId", u."LabelCopy",
                       u."IsPaused", u."PausedByPlan", u."Version", (SELECT count(*)::int FROM ranked) AS "Active"
                FROM upd u
                WHERE u."Id" = {id}
                """).ToListAsync(c);
            if (updated.Count == 0) return await LostRaceAsync(id, ctx, savesLeft, current => !current.PausedByPlan, c);
            await tx.CommitAsync(c);
            return new Be2Result<SpecialPriceWriteDto>(
                new SpecialPriceWriteDto(ToItem(updated[0]), Meters(updated[0].Active, ctx.Limits.SpecialPrices), Math.Max(0, savesLeft - 1)),
                DistrictId: ctx.DistrictId, Changed: true);
        }, ct, logger);
    }

    // ---------- helpers ----------

    public static SpecialPriceItemDto ToItem(SpecialPriceRow r)
        => new(r.Id, r.DateFrom, r.DateTo, StaySpecialPriceRules.Nights(r.DateFrom, r.DateTo), r.Direction, r.Percent, r.LabelCopy,
            r.IsPaused, r.PausedByPlan, StaySpecialPriceRules.IsLive(r.IsPaused, r.PausedByPlan), r.Version);

    public static SpecialPriceItemDto ToItem(StaySpecialPrice r)
        => new(r.Id, r.DateFrom, r.DateTo, StaySpecialPriceRules.Nights(r.DateFrom, r.DateTo), r.Direction, r.Percent, r.LabelCopy,
            r.IsPaused, r.PausedByPlan, StaySpecialPriceRules.IsLive(r.IsPaused, r.PausedByPlan), r.Version);

    public static IReadOnlyList<MeterDto> Meters(int used, int limit) => [new MeterDto(PlanFeatureKeys.SpecialPrices, used, limit)];

    // Level 5: the stay row FOR UPDATE with the counter (the newest row version after a wait), optionally with one rule of the stay.
    // The rule columns may be from before the wait; every write that uses them is guarded by its Version.
    private async Task<SpecialPriceLockRow?> LockAsync(Guid stayId, Guid? ruleId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<SpecialPriceLockRow>($"""
            SELECT s."SpecialPriceSavesDay" AS "SavesDay", s."SpecialPriceSavesCount" AS "SavesCount",
                   p."Id" AS "RuleId", p."StayListingId" AS "RuleStayId", p."DateFrom" AS "RuleFrom", p."DateTo" AS "RuleTo",
                   p."Direction" AS "RuleDirection", p."Percent" AS "RulePercent", p."FestivalDateId" AS "RuleFestivalId",
                   p."LabelCopy" AS "RuleLabel", p."IsPaused" AS "RulePaused", p."PausedByPlan" AS "RulePausedByPlan", p."Version" AS "RuleVersion"
            FROM "StayListings" s
            LEFT JOIN "StaySpecialPrices" p ON p."Id" = {ruleId} AND p."StayListingId" = s."Id"
            WHERE s."Id" = {stayId} AND s."IsDeleted" = false
            FOR UPDATE OF s
            """).ToListAsync(ct);
        return rows.Count == 1 ? rows[0] : null;
    }

    // One statement after the lock: active rules (incl. paused and plan-paused) and the first rule overlapping the nights.
    private async Task<SpecialPriceCheckRow> CheckAsync(Guid stayId, DateOnly today, Guid selfId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<SpecialPriceCheckRow>($"""
            SELECT (SELECT count(*)::int FROM "StaySpecialPrices" a WHERE a."StayListingId" = {stayId} AND a."DateTo" >= {today}) AS "Active",
                   o."Id" AS "OverlapId", o."LabelCopy" AS "OverlapLabel", o."DateFrom" AS "OverlapFrom", o."DateTo" AS "OverlapTo"
            FROM (SELECT 1) one
            LEFT JOIN LATERAL (
                SELECT p."Id", p."LabelCopy", p."DateFrom", p."DateTo"
                FROM "StaySpecialPrices" p
                WHERE p."StayListingId" = {stayId} AND p."DateTo" >= {today} AND p."Id" <> {selfId}
                  AND p."DateFrom" <= {to} AND p."DateTo" >= {from}
                ORDER BY p."DateFrom", p."Id"
                LIMIT 1
            ) o ON true
            """).ToListAsync(ct);
        return rows[0];
    }

    private async Task<SpecialPriceRow?> FindAsync(Guid id, CancellationToken ct)
        => (await db.Database.SqlQuery<SpecialPriceRow>($"""
            SELECT p."Id", p."StayListingId", p."DateFrom", p."DateTo", p."Direction", p."Percent", p."FestivalDateId", p."LabelCopy",
                   p."IsPaused", p."PausedByPlan", p."Version", 0 AS "Active"
            FROM "StaySpecialPrices" p WHERE p."Id" = {id}
            """).ToListAsync(ct)).FirstOrDefault();

    private Task<int> ActiveCountAsync(Guid stayId, DateOnly today, CancellationToken ct)
        => db.Set<StaySpecialPrice>().CountAsync(p => p.StayListingId == stayId && p.DateTo >= today, ct);

    // The rule read with the lock can be older than the lock (LockAsync), so the guarded write found another version. Read under
    // the lock: already in the asked state = a replay (4.2, the same tap from two phones), else the stored row as StaleEdit.
    private async Task<Be2Result<SpecialPriceWriteDto>> LostRaceAsync(
        Guid id, OwnerContext ctx, int savesLeft, Func<SpecialPriceRow, bool> alreadyDone, CancellationToken ct)
    {
        if (await FindAsync(id, ct) is not { } current || current.StayListingId != ctx.ListingId)
            return Be2Result<SpecialPriceWriteDto>.Missing(NotFoundMessage);
        if (!alreadyDone(current))
            return Be2Result<SpecialPriceWriteDto>.Fail(OwnerErrorTypes.StaleEdit, new StaleEditDto<SpecialPriceItemDto>(ToItem(current)));
        var active = await ActiveCountAsync(ctx.ListingId, ctx.Today, ct);
        return new(new SpecialPriceWriteDto(ToItem(current), Meters(active, ctx.Limits.SpecialPrices), savesLeft), Replay: true);
    }

    // 13.8 delete-on-act: a saved rule touching a festival drops the owner's FestivalReminder notice of that festival.
    private Task ClearFestivalNoticesAsync(Guid me, DateOnly from, DateOnly to, IReadOnlyList<FestivalDto> stateFestivals, CancellationToken ct)
        => notifier.ClearManyAsync(NoticeKinds.FestivalReminder, StaySpecialPriceRules.TouchingFestivals(from, to, stateFestivals), me, ct);

    private static Be2Result<T> SavesRefusal<T>(SpecialPriceLockRow lockRow, DateOnly today)
        => Be2Result<T>.Fail(OwnerErrorTypes.SpecialPriceSavesLimit,
            new DailyLimitRefusalDto(OwnerLimits.SpecialSavesPerDay, StaySpecialPriceRules.SavesUsed(lockRow.SavesDay, lockRow.SavesCount, today)));

    private static Be2Result<T> LimitRefusal<T>(int used, OwnerContext ctx)
        => Be2Result<T>.Fail(OwnerErrorTypes.SpecialPriceLimit,
            OwnerErrorTypes.LimitMessage(OwnerErrorTypes.SpecialPriceLimit, ctx.Limits.SpecialPrices, ctx.Limits.CanUpgrade),
            new PlanLimitRefusalDto(PlanFeatureKeys.SpecialPrices, ctx.Limits.SpecialPrices, used, ctx.Limits.CanUpgrade));

    private static Be2Result<T> Overlap<T>(Guid id, SpecialPriceCheckRow check)
        => Be2Result<T>.Fail(OwnerErrorTypes.SpecialPriceOverlap,
            new SpecialPriceOverlapDto(id, check.OverlapLabel, check.OverlapFrom!.Value, check.OverlapTo!.Value));
}

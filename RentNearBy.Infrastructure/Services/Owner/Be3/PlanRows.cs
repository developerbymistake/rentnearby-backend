using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner.Be3;

// Contract 3.3: rows hidden by the plan, read and written set-based with raw SQL (this file sees hidden rows on purpose).
// Every write needs the caller's transaction and the listing row lock (level 5); rows follow in the 4.1 table order.
public static class PlanRows
{
    public sealed class RowState
    {
        public Guid Id { get; set; }
        public bool Hidden { get; set; }
        public int Index { get; set; }
    }

    public sealed class RouteState
    {
        public Guid Id { get; set; }
        public bool Hidden { get; set; }
        public int Index { get; set; }
        public long? FromCityKey { get; set; }
        public long? ToCityKey { get; set; }
    }

    private sealed class JobState
    {
        public Guid Id { get; set; }
        public bool Inactive { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class RuleState
    {
        public Guid Id { get; set; }
        public bool PausedByPlan { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public const string PhotosSql = """
        SELECT "Id", "HiddenByPlan" AS "Hidden", "PhotoOrder" AS "Index" FROM "StayPhotos" WHERE "StayListingId" = @l
        """;

    public const string RoomsSql = """
        SELECT "Id", "HiddenByPlan" AS "Hidden", "RoomIndex"::int AS "Index" FROM "StayRooms" WHERE "StayListingId" = @l
        """;

    public const string RoutesSql = """
        SELECT "Id", "HiddenByPlan" AS "Hidden", "RouteIndex"::int AS "Index", "FromCityKey", "ToCityKey"
        FROM "TaxiRoutes" WHERE "TaxiListingId" = @l
        """;

    private const string ActiveRulesSql = """
        SELECT "Id", "PausedByPlan", "CreatedAt" FROM "StaySpecialPrices" WHERE "StayListingId" = @l AND "DateTo" >= @today
        """;

    public static IReadOnlyList<PlanRow> AsRows(IEnumerable<RowState> rows) => rows.Select(r => new PlanRow(r.Id, r.Hidden, r.Index)).ToList();

    public static IReadOnlyList<PlanRow> AsRows(IEnumerable<RouteState> rows) => rows.Select(r => new PlanRow(r.Id, r.Hidden, r.Index)).ToList();

    public static Task<List<RowState>> PhotosAsync(ApplicationDbContext db, Guid stayId, CancellationToken ct = default)
        => db.Database.SqlQueryRaw<RowState>(PhotosSql, Listing(stayId)).ToListAsync(ct);

    public static Task<List<RowState>> RoomsAsync(ApplicationDbContext db, Guid stayId, CancellationToken ct = default)
        => db.Database.SqlQueryRaw<RowState>(RoomsSql, Listing(stayId)).ToListAsync(ct);

    public static Task<List<RouteState>> RoutesAsync(ApplicationDbContext db, Guid taxiId, CancellationToken ct = default)
        => db.Database.SqlQueryRaw<RouteState>(RoutesSql, Listing(taxiId)).ToListAsync(ct);

    // Rooms: two statements so the (StayListingId, RoomIndex) unique index never sees a duplicate (shift, then final values).
    public static Task<int> WriteRoomsAsync(ApplicationDbContext db, Guid stayId, IReadOnlyList<PlanRowTarget> all, CancellationToken ct = default)
        => WriteIndexedAsync(db, "\"StayRooms\"", "\"StayListingId\"", "\"RoomIndex\"", stayId, all, ct);

    public static Task<int> WriteRoutesAsync(ApplicationDbContext db, Guid taxiId, IReadOnlyList<PlanRowTarget> all, CancellationToken ct = default)
        => WriteIndexedAsync(db, "\"TaxiRoutes\"", "\"TaxiListingId\"", "\"RouteIndex\"", taxiId, all, ct);

    // Photos: one statement (no unique index on PhotoOrder). Only the given rows are written.
    public static async Task<int> WritePhotosAsync(ApplicationDbContext db, Guid stayId, IReadOnlyList<PlanRowTarget> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;
        return await db.Database.ExecuteSqlRawAsync("""
            UPDATE "StayPhotos" p SET "HiddenByPlan" = v.hidden, "PhotoOrder" = v.idx
            FROM unnest(@ids, @hidden, @idx) AS v(id, hidden, idx)
            WHERE p."Id" = v.id AND p."StayListingId" = @l
            """, [Listing(stayId), Ids(rows), Hidden(rows), Indexes(rows)], ct);
    }

    // MinPricePerNight over visible rooms only (the cache row and the public lists read it).
    public static Task<int> RecomputeMinPriceAsync(ApplicationDbContext db, Guid stayId, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync("""
            UPDATE "StayListings" s SET "MinPricePerNight" =
                (SELECT min(r."PricePerNight") FROM "StayRooms" r WHERE r."StayListingId" = s."Id" AND NOT r."HiddenByPlan")
            WHERE s."Id" = @l
            """, [Listing(stayId)], ct);

    public static Task<int> RecomputeMinFareAsync(ApplicationDbContext db, Guid taxiId, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync("""
            UPDATE "TaxiListings" t SET "MinFare" =
                (SELECT min(r."Fare") FROM "TaxiRoutes" r WHERE r."TaxiListingId" = t."Id" AND NOT r."HiddenByPlan")
            WHERE t."Id" = @l
            """, [Listing(taxiId)], ct);

    // Special prices (contract 3.4): the last added stay live. One read, then one statement for the rules whose PausedByPlan flips. The owner's swap
    // (P6) is never undone because live rows rank first. Returns the rows changed.
    public static async Task<int> ApplySpecialPricesAsync(ApplicationDbContext db, Guid stayId, int limit, bool hide, DateOnly today, CancellationToken ct = default)
        => (await ApplySpecialPricesEffectAsync(db, stayId, limit, hide, today, ct)).Changed;

    public sealed record RulesEffect(int Changed, int PausedBefore, int PausedAfter);

    // Same pass, with the plan-paused counts before and after so the plan effect counts paused rules like rooms, photos and jobs.
    public static async Task<RulesEffect> ApplySpecialPricesEffectAsync(ApplicationDbContext db, Guid stayId, int limit, bool hide, DateOnly today, CancellationToken ct = default)
    {
        var rules = await db.Database.SqlQueryRaw<RuleState>(ActiveRulesSql, Listing(stayId), Today(today)).ToListAsync(ct);
        var paused = PlanRuleChoice.PausedByPlan(rules.Select(r => new PlanRuleRow(r.Id, r.PausedByPlan, r.CreatedAt)), limit, hide);
        var before = rules.Count(r => r.PausedByPlan);
        var after = paused.Values.Count(v => v);
        var flips = rules.Where(r => paused[r.Id] != r.PausedByPlan).Select(r => (r.Id, paused[r.Id])).ToList();
        if (flips.Count == 0) return new RulesEffect(0, before, after);
        var changed = await db.Database.ExecuteSqlRawAsync("""
            UPDATE "StaySpecialPrices" p SET "PausedByPlan" = v.paused, "Version" = p."Version" + 1, "UpdatedAt" = now()
            FROM unnest(@ids, @paused) AS v(id, paused)
            WHERE p."Id" = v.id AND p."StayListingId" = @l
            """,
            [
                Listing(stayId),
                new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = flips.Select(f => f.Id).ToArray() },
                new NpgsqlParameter("paused", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = flips.Select(f => f.Item2).ToArray() },
            ], ct);
        return new RulesEffect(changed, before, after);
    }

    // Jobs (PlanJobChoice): one read of the owner's open jobs, then one statement for the jobs whose status flips. Closed jobs never
    // move. Runs under the caller's stay lock (the job create takes the same lock); returns the rows changed.
    public const string OpenJobsSql = """
        SELECT "Id", "Status" = 'Inactive' AS "Inactive", "CreatedAt" FROM "StayJobs" WHERE "UserId" = @u AND "Status" IN ('Active', 'Inactive')
        """;

    public sealed record JobsEffect(int Changed, int InactiveBefore, int InactiveAfter);

    public static async Task<JobsEffect> ApplyJobsAsync(ApplicationDbContext db, Guid userId, int limit, bool hide, CancellationToken ct = default)
    {
        var jobs = await db.Database.SqlQueryRaw<JobState>(OpenJobsSql, new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = userId }).ToListAsync(ct);
        var inactive = PlanJobChoice.Inactive(jobs.Select(j => new PlanJobRow(j.Id, j.Inactive, j.CreatedAt)), limit, hide);
        var flips = jobs.Where(j => inactive[j.Id] != j.Inactive).ToList();
        var before = jobs.Count(j => j.Inactive);
        var after = inactive.Values.Count(v => v);
        if (flips.Count == 0) return new(0, before, after);
        var changed = await db.Database.ExecuteSqlRawAsync("""
            UPDATE "StayJobs" j SET "Status" = CASE WHEN v.inactive THEN 'Inactive' ELSE 'Active' END, "UpdatedAt" = now()
            FROM unnest(@ids, @inactive) AS v(id, inactive)
            WHERE j."Id" = v.id AND j."UserId" = @u AND j."Status" IN ('Active', 'Inactive')
            """,
            [
                new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = userId },
                new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = flips.Select(f => f.Id).ToArray() },
                new NpgsqlParameter("inactive", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = flips.Select(f => inactive[f.Id]).ToArray() },
            ], ct);
        return new(changed, before, after);
    }

    private static async Task<int> WriteIndexedAsync(
        ApplicationDbContext db, string table, string listingColumn, string indexColumn, Guid listingId, IReadOnlyList<PlanRowTarget> all, CancellationToken ct)
    {
        if (all.Count == 0) return 0;
#pragma warning disable EF1002 // table and column names are the fixed literals of the two callers above; values are parameters.
        await db.Database.ExecuteSqlRawAsync(
            $"UPDATE {table} SET {indexColumn} = {indexColumn} + {PlanRowBands.RenumberShift} WHERE {listingColumn} = @l",
            [Listing(listingId)], ct);
        return await db.Database.ExecuteSqlRawAsync($"""
            UPDATE {table} t SET "HiddenByPlan" = v.hidden, {indexColumn} = v.idx::smallint
            FROM unnest(@ids, @hidden, @idx) AS v(id, hidden, idx)
            WHERE t."Id" = v.id AND t.{listingColumn} = @l
            """, [Listing(listingId), Ids(all), Hidden(all), Indexes(all)], ct);
#pragma warning restore EF1002
    }

    private static NpgsqlParameter Listing(Guid id) => new("l", NpgsqlDbType.Uuid) { Value = id };

    private static NpgsqlParameter Today(DateOnly today) => new("today", NpgsqlDbType.Date) { Value = today };

    private static NpgsqlParameter Ids(IReadOnlyList<PlanRowTarget> rows)
        => new("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(r => r.Id).ToArray() };

    private static NpgsqlParameter Hidden(IReadOnlyList<PlanRowTarget> rows)
        => new("hidden", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = rows.Select(r => r.Hidden).ToArray() };

    private static NpgsqlParameter Indexes(IReadOnlyList<PlanRowTarget> rows)
        => new("idx", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = rows.Select(r => r.Index).ToArray() };
}

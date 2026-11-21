using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using StackExchange.Redis;
using static RentNearBy.Infrastructure.Services.Owner.Be4.OwnerJobSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// Contract 5.1 owner.stats.flush (00:30 IST). Shown: HSCAN the Redis day hash of yesterday (and the day before, while it exists) in
// pages of 1000, ONE upsert per page; a hash two days old is deleted after its flush. RequestsNoRoute: ONE statement for yesterday
// (requests created that IST day whose From key matches a visible route of the driver and whose To key matches none of his visible
// routes). No Redis = Shown stays empty and the card shows the plain line.
public sealed class OwnerWeeklyStatsFlushJob(
    ApplicationDbContext db,
    IOptions<OwnerFeatureOptions> options,
    TimeProvider clock,
    ILogger<OwnerWeeklyStatsFlushJob> logger,
    IConnectionMultiplexer? redis = null) : IBuddyFeatureJob
{
    public const int PageSize = 1000;

    public string Name => "owner.stats.flush";

    public TimeSpan Interval => TimeSpan.FromDays(1);

    public TimeOnly? DailyAtIst => ParseIstTime(options.Value.StatsFlushIst) ?? new TimeOnly(0, 30);

    internal static readonly string ShownSql = $"""
        INSERT INTO "OwnerWeeklyStats" ("ListingKind", "ListingId", "Day", "Shown", "RequestsNoRoute")
        SELECT '{ListingKinds.Stay}', u.id, @day, u.n, 0
        FROM unnest(@ids, @counts) AS u(id, n)
        WHERE EXISTS (SELECT 1 FROM "StayListings" s WHERE s."Id" = u.id AND NOT s."IsDeleted")
        ON CONFLICT ("ListingKind", "ListingId", "Day") DO UPDATE SET "Shown" = EXCLUDED."Shown"
        """;

    internal static readonly string NoRouteSql = $"""
        INSERT INTO "OwnerWeeklyStats" ("ListingKind", "ListingId", "Day", "Shown", "RequestsNoRoute")
        SELECT '{ListingKinds.Taxi}', l."Id", @day, 0, count(DISTINCT r."Id")::int
        FROM "TaxiRequests" r
        JOIN "TaxiRoutes" rt ON rt."FromCityKey" = r."MatchFromCityKey" AND NOT rt."HiddenByPlan"
        JOIN "TaxiListings" l ON l."Id" = rt."TaxiListingId" AND NOT l."IsDeleted" AND l."Status" = 'Approved'
        WHERE r."CreatedAt" >= @d0 AND r."CreatedAt" < @d1 AND r."MatchFromCityKey" IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM "TaxiRoutes" x
                          WHERE x."TaxiListingId" = l."Id" AND NOT x."HiddenByPlan" AND x."ToCityKey" = r."MatchToCityKey")
        GROUP BY l."Id"
        ON CONFLICT ("ListingKind", "ListingId", "Day") DO UPDATE SET "RequestsNoRoute" = EXCLUDED."RequestsNoRoute"
        """;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var today = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);
        var rows = 0;
        foreach (var day in new[] { today.AddDays(-1), today.AddDays(-2) }) // batch-loop
            rows += await FlushShownAsync(day, deleteAfter: day <= today.AddDays(-2), ct); // batch-loop

        var yesterday = today.AddDays(-1);
        var (d0, d1) = IstDayUtc(yesterday);
        rows += await db.Database.ExecuteSqlRawAsync(NoRouteSql, [Date("day", yesterday), Utc("d0", d0), Utc("d1", d1)], ct);
        return rows;
    }

    private async Task<int> FlushShownAsync(DateOnly day, bool deleteAfter, CancellationToken ct)
    {
        if (redis is null || !redis.IsConnected) return 0;
        var key = OwnerShownCounter.Key(day);
        var rdb = redis.GetDatabase();
        try
        {
            if (!await rdb.KeyExistsAsync(key)) return 0;
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "owner.stats.flush: Redis unavailable, Shown for {Day} not flushed", day);
            return 0;
        }

        var written = 0;
        var page = new List<(Guid Id, int Count)>(PageSize);
        await foreach (var entry in rdb.HashScanAsync(key, pageSize: PageSize).WithCancellation(ct)) // batch-loop
        {
            if (Guid.TryParse(entry.Name.ToString(), out var id) && entry.Value.TryParse(out long n) && n > 0)
                page.Add((id, (int)Math.Min(n, int.MaxValue)));
            if (page.Count < PageSize) continue;
            written += await UpsertAsync(day, page, ct); // batch-loop
            page.Clear();
        }
        if (page.Count > 0) written += await UpsertAsync(day, page, ct);
        if (deleteAfter) await rdb.KeyDeleteAsync(key);
        return written;
    }

    private Task<int> UpsertAsync(DateOnly day, IReadOnlyList<(Guid Id, int Count)> page, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(ShownSql,
        [
            Date("day", day),
            new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = page.Select(p => p.Id).ToArray() },
            new NpgsqlParameter("counts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = page.Select(p => p.Count).ToArray() },
        ], ct);
}

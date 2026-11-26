using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

// Contract 1.5 P1: every active admin route with taxis today, in admin order. ONE set-based statement (CROSS JOIN LATERAL over
// ix_taxiroutes_to_from_city plus the availability probes), cached 300 s per IST day, stamped by search + avail + popular gens.
// A taxi serves the pair when a visible route has the two city keys in either direction and the taxi is available today.
public sealed class PopularRouteReader(ApplicationDbContext db, ICityResolver resolver, TimeProvider clock, IConnectionMultiplexer? redis = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(300);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly IReadOnlyList<string> GenKeys =
        [ListingCacheKeys.TaxiSearchGen, ListingCacheKeys.TaxiAvailGen, ListingCacheKeys.TaxiPopularGen];

    public static readonly string Sql = $"""
        SELECT p."Id", p."FromCityKey", p."ToCityKey", a."Taxis", a."MinFare"
        FROM "PopularRoutes" p
        CROSS JOIN LATERAL (
            SELECT count(DISTINCT r."TaxiListingId")::int AS "Taxis", min(r."Fare")::int AS "MinFare"
            FROM "TaxiRoutes" r
            JOIN "TaxiListings" t ON t."Id" = r."TaxiListingId"
            WHERE NOT r."HiddenByPlan" AND r."ToCityKey" IS NOT NULL AND r."FromCityKey" IS NOT NULL
              AND ((r."ToCityKey" = p."ToCityKey" AND r."FromCityKey" = p."FromCityKey")
                OR (r."ToCityKey" = p."FromCityKey" AND r."FromCityKey" = p."ToCityKey"))
              AND {TaxiAvailabilitySql.PublicOn("t", TaxiAvailabilitySql.DayParameter)}
        ) a
        WHERE p."IsActive" AND a."Taxis" > 0
        ORDER BY p."SortOrder", p."Id"
        """;

    public sealed class Row
    {
        public Guid Id { get; set; }
        public long FromCityKey { get; set; }
        public long ToCityKey { get; set; }
        public int Taxis { get; set; }
        public int? MinFare { get; set; }
    }

    public async Task<PopularRoutesPublicDto> ReadAsync(CancellationToken ct = default)
    {
        var today = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);
        var key = ListingCacheKeys.TaxiPopular(today);
        var (cached, generation) = await FeedCache.ReadManyAsync(redis, key, GenKeys);
        if (cached != null)
        {
            try
            {
                if (JsonSerializer.Deserialize<PopularRoutesPublicDto>(cached, Json) is { } hit) return hit;
            }
            catch (JsonException) { }
        }

        var rows = await db.Database.SqlQueryRaw<Row>(Sql, TaxiAvailabilitySql.Day(today)).ToListAsync(ct);
        var (items, available) = await NameAsync(rows, resolver);
        var dto = new PopularRoutesPublicDto(TaxiSearchDate.Text(today), items);
        if (available) await FeedCache.WriteAsync(redis, key, generation, JsonSerializer.Serialize(dto, Json), Ttl);
        return dto;
    }

    // Names and pins from the cached city catalog; a key that is no longer live drops the route; no catalog = empty, not cached.
    public static async Task<(IReadOnlyList<PopularRoutePublicDto> Items, bool Available)> NameAsync(IReadOnlyList<Row> rows, ICityResolver resolver)
    {
        if (rows.Count == 0) return ([], true);
        var cities = new Dictionary<long, TripCityInfo?>();
        foreach (var k in rows.SelectMany(r => new[] { r.FromCityKey, r.ToCityKey }).Distinct())
        {
            var pin = await resolver.FindAsync(k);
            if (!pin.Available) return ([], false);
            cities[k] = pin.City;
        }
        var items = new List<PopularRoutePublicDto>(rows.Count);
        foreach (var r in rows)
        {
            if (cities.GetValueOrDefault(r.FromCityKey) is not { } from || cities.GetValueOrDefault(r.ToCityKey) is not { } to) continue;
            items.Add(new PopularRoutePublicDto(r.Id, r.FromCityKey, from.Name, from.Latitude, from.Longitude,
                r.ToCityKey, to.Name, to.Latitude, to.Longitude, r.MinFare, r.Taxis));
        }
        return (items, true);
    }
}

// Contract 1.6 AD1-AD5. Popular routes exist only through these calls. Writes that touch the row count or the order take one
// transaction-scoped advisory lock, so the 30-row cap and the full-list reorder cannot race; the unique pair index refuses duplicates.
public sealed class PopularRouteAdminStore(ApplicationDbContext db, ICityResolver resolver)
{
    private const long LockKey = 7_310_026_112_601;
    public const string CitiesUnavailableMessage = "Cities are not available right now";

    private const string Columns = """p."Id", p."FromCityKey", p."FromCityName", p."ToCityKey", p."ToCityName", p."SortOrder", p."IsActive" """;

    public const string ListSql = $"""SELECT {Columns} FROM "PopularRoutes" p ORDER BY p."SortOrder", p."Id" """;

    public static readonly string InsertSql = $"""
        INSERT INTO "PopularRoutes" AS p ("Id", "FromCityKey", "ToCityKey", "FromCityName", "ToCityName", "SortOrder", "IsActive", "CreatedAt", "UpdatedAt")
        SELECT @id, @from, @to, @fromName, @toName, COALESCE(max(q."SortOrder"), 0) + 1, true, now(), now()
        FROM "PopularRoutes" q
        HAVING count(*) < {PopularRouteLimits.Max}
        RETURNING {Columns}
        """;

    public const string DeleteSql = """DELETE FROM "PopularRoutes" WHERE "Id" = @id""";

    public const string ActiveSql = $"""
        UPDATE "PopularRoutes" AS p SET "IsActive" = @active, "UpdatedAt" = now() WHERE p."Id" = @id
        RETURNING {Columns}
        """;

    public const string IdsSql = """SELECT "Id" AS "Value" FROM "PopularRoutes" """;

    public const string ReorderSql = """
        UPDATE "PopularRoutes" AS p SET "SortOrder" = o.ord::int, "UpdatedAt" = now()
        FROM unnest(@ids) WITH ORDINALITY AS o(id, ord)
        WHERE p."Id" = o.id AND p."SortOrder" IS DISTINCT FROM o.ord::int
        """;

    public sealed class Row
    {
        public Guid Id { get; set; }
        public long FromCityKey { get; set; }
        public string FromCityName { get; set; } = string.Empty;
        public long ToCityKey { get; set; }
        public string ToCityName { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public bool IsActive { get; set; }

        public PopularRouteAdminDto ToDto() => new(Id, FromCityKey, FromCityName, ToCityKey, ToCityName, SortOrder, IsActive);
    }

    public async Task<IReadOnlyList<PopularRouteAdminDto>> ListAsync(CancellationToken ct = default)
        => (await db.Database.SqlQueryRaw<Row>(ListSql).ToListAsync(ct)).Select(r => r.ToDto()).ToList();

    public static Be1Error? CheckPair(long? fromKey, long? toKey)
    {
        if (fromKey is not > 0 || toKey is not > 0) return Be1Error.Of(OwnerErrorTypes.PopularRouteCityUnknown);
        return fromKey == toKey ? Be1Error.Of(OwnerErrorTypes.PopularRouteSameCity) : null;
    }

    public async Task<Be1Result<PopularRouteAdminDto>> CreateAsync(long? fromKey, long? toKey, CancellationToken ct = default)
    {
        if (CheckPair(fromKey, toKey) is { } bad) return bad;
        var from = await resolver.FindAsync(fromKey!.Value);
        var to = await resolver.FindAsync(toKey!.Value);
        if (!from.Available || !to.Available) return new Be1Error(TripErrorTypes.CitiesUnavailable, CitiesUnavailableMessage);
        if (from.City is not { } f || to.City is not { } t) return Be1Error.Of(OwnerErrorTypes.PopularRouteCityUnknown);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);
        List<Row> rows;
        try
        {
            rows = await db.Database.SqlQueryRaw<Row>(InsertSql,
                Uuid("id", Guid.NewGuid()), Long("from", fromKey.Value), Long("to", toKey.Value),
                Text("fromName", Clip(f.Name)), Text("toName", Clip(t.Name))).ToListAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            return Be1Error.Of(OwnerErrorTypes.PopularRouteDuplicate);
        }
        if (rows.Count == 0) return Be1Error.Of(OwnerErrorTypes.PopularRouteLimit);
        await tx.CommitAsync(ct);
        return Be1Result<PopularRouteAdminDto>.Success(rows[0].ToDto());
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
        => await db.Database.ExecuteSqlRawAsync(DeleteSql, [Uuid("id", id)], ct);

    public async Task<PopularRouteAdminDto?> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<Row>(ActiveSql, Uuid("id", id),
            new NpgsqlParameter("active", NpgsqlDbType.Boolean) { Value = active }).ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0].ToDto();
    }

    // ids = the full list in the wanted order; another id set (added, deleted or repeated meanwhile) is PopularRouteOrderStale.
    public async Task<Be1Result<IReadOnlyList<PopularRouteAdminDto>>> ReorderAsync(IReadOnlyList<Guid>? ids, CancellationToken ct = default)
    {
        var wanted = ids ?? [];
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);
        var current = await db.Database.SqlQueryRaw<Guid>(IdsSql).ToListAsync(ct);
        if (!SameSet(wanted, current)) return Be1Error.Of(OwnerErrorTypes.PopularRouteOrderStale);
        if (wanted.Count > 0)
            await db.Database.ExecuteSqlRawAsync(ReorderSql,
                [new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = wanted.ToArray() }], ct);
        var list = await ListAsync(ct);
        await tx.CommitAsync(ct);
        return Be1Result<IReadOnlyList<PopularRouteAdminDto>>.Success(list);
    }

    public static bool SameSet(IReadOnlyList<Guid> wanted, IReadOnlyCollection<Guid> current)
    {
        var w = wanted.ToHashSet();
        return w.Count == wanted.Count && w.Count == current.Count && w.SetEquals(current);
    }

    private Task LockAsync(CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(@k)", [new NpgsqlParameter("k", NpgsqlDbType.Bigint) { Value = LockKey }], ct);

    private static string Clip(string name) => name.Length <= PopularRouteLimits.CityNameMax ? name : name[..PopularRouteLimits.CityNameMax];

    private static bool IsUniqueViolation(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) return true;
        return false;
    }

    private static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };

    private static NpgsqlParameter Long(string name, long value) => new(name, NpgsqlDbType.Bigint) { Value = value };

    private static NpgsqlParameter Text(string name, string value) => new(name, NpgsqlDbType.Varchar) { Value = value };
}

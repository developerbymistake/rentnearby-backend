using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

// Generic distance-ordered paging over the GiST indexes. Table text comes from the enum only, every value is a parameter.
public sealed class NearbyFeedQuery(ApplicationDbContext context) : INearbyFeedQuery
{
    private const string Point = "ST_SetSRID(ST_MakePoint(@lng, @lat), 4326)::geography";

    private sealed class Raw
    {
        public Guid Id { get; set; }
        public double DistanceMeters { get; set; }
    }

    private sealed record Shape(string From, string IdCol, string Loc, string Live);

    private const string DistrictOk = "(d.\"Id\" IS NULL OR d.\"IsActive\")";

    private static Shape ShapeOf(NearbyKind kind) => kind switch
    {
        NearbyKind.Stay => new(
            "\"StayListings\" s LEFT JOIN \"Districts\" d ON d.\"Id\" = s.\"DistrictId\"",
            "s.\"Id\"", "s.\"Location\"",
            "s.\"Status\" = 'Approved' AND s.\"AcceptingGuests\" AND NOT s.\"IsDeleted\" AND s.\"Location\" IS NOT NULL AND " + DistrictOk),
        NearbyKind.Taxi => new(
            "\"TaxiListings\" t LEFT JOIN \"Districts\" d ON d.\"Id\" = t.\"DistrictId\"",
            "t.\"Id\"", "t.\"LocationCoarse\"",
            "t.\"Status\" = 'Approved' AND t.\"IsAvailable\" AND NOT t.\"IsDeleted\" AND t.\"LocationCoarse\" IS NOT NULL AND " + DistrictOk),
        NearbyKind.StayJob => new(
            "\"StayJobs\" j JOIN \"StayListings\" s ON s.\"Id\" = j.\"StayListingId\" LEFT JOIN \"Districts\" d ON d.\"Id\" = s.\"DistrictId\"",
            "j.\"Id\"", "s.\"Location\"",
            "j.\"Status\" = 'Active' AND s.\"Status\" = 'Approved' AND NOT s.\"IsDeleted\" AND s.\"Location\" IS NOT NULL"
            + " AND EXISTS (SELECT 1 FROM \"Users\" u WHERE u.\"Id\" = j.\"UserId\" AND u.\"IsActive\") AND " + DistrictOk),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Dist(Shape s) => $"({s.Loc} <-> {Point})";

    private static void AddFilters(NearbyKind kind, Shape s, NearbyFilter f, StringBuilder sql, List<NpgsqlParameter> ps)
    {
        if (f.ExcludeIds is { Count: > 0 } ex)
        {
            sql.Append($" AND {s.IdCol} <> ALL(@ex)");
            ps.Add(new NpgsqlParameter("ex", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ex.ToArray() });
        }
        switch (kind)
        {
            case NearbyKind.Stay:
                if (f.StayTypeId is { } type)
                {
                    sql.Append(" AND s.\"StayTypeId\" = @stayType");
                    ps.Add(new NpgsqlParameter("stayType", type));
                }
                if (!string.IsNullOrEmpty(f.Meals))
                {
                    sql.Append(" AND EXISTS (SELECT 1 FROM \"StayRooms\" r WHERE r.\"StayListingId\" = s.\"Id\" AND r.\"Meals\" = @meals)");
                    ps.Add(new NpgsqlParameter("meals", f.Meals));
                }
                break;
            case NearbyKind.Taxi:
                if (!string.IsNullOrEmpty(f.Category))
                {
                    sql.Append(" AND EXISTS (SELECT 1 FROM \"TaxiVehicleTypes\" v WHERE v.\"Id\" = t.\"VehicleTypeId\" AND v.\"Category\" = @category)");
                    ps.Add(new NpgsqlParameter("category", f.Category));
                }
                break;
            case NearbyKind.StayJob:
                if (f.DepartmentId is { } dep)
                {
                    sql.Append(" AND j.\"JobDepartmentId\" = @dep");
                    ps.Add(new NpgsqlParameter("dep", dep));
                }
                if (f.MinExperience is { } minE)
                {
                    sql.Append(" AND j.\"MinimumExperienceYears\" >= @minExp");
                    ps.Add(new NpgsqlParameter("minExp", minE));
                }
                if (f.MaxExperience is { } maxE)
                {
                    sql.Append(" AND j.\"MinimumExperienceYears\" <= @maxExp");
                    ps.Add(new NpgsqlParameter("maxExp", maxE));
                }
                if (f.MinSalary is { } sal)
                {
                    sql.Append(" AND j.\"MaxSalary\" >= @minSalary");
                    ps.Add(new NpgsqlParameter("minSalary", sal));
                }
                break;
        }
    }

    private static List<NpgsqlParameter> Centre(double lat, double lng)
        => [new NpgsqlParameter("lat", lat), new NpgsqlParameter("lng", lng)];

    public async Task<IReadOnlyList<NearbyRow>> PageAsync(NearbyKind kind, double lat, double lng, NearbyToken? after, int take,
        NearbyFilter filter, CancellationToken ct = default)
    {
        var s = ShapeOf(kind);
        var d = Dist(s);
        var ps = Centre(lat, lng);
        var sql = new StringBuilder($"SELECT {s.IdCol} AS \"Id\", {d}::float8 AS \"DistanceMeters\" FROM {s.From} WHERE {s.Live}");
        if (after != null)
        {
            sql.Append($" AND ({d} > @afterD OR ({d} = @afterD AND {s.IdCol} > @afterId))");
            ps.Add(new NpgsqlParameter("afterD", after.DistanceMeters));
            ps.Add(new NpgsqlParameter("afterId", after.Id));
        }
        AddFilters(kind, s, filter, sql, ps);
        sql.Append($" ORDER BY {d}, {s.IdCol} LIMIT @take");
        ps.Add(new NpgsqlParameter("take", Math.Clamp(take, 1, NearbyRules.MaxPageSize + 1)));
        return await Run(sql.ToString(), ps, ct);
    }

    public async Task<IReadOnlyList<NearbyRow>> FeaturedAsync(NearbyKind kind, double lat, double lng, double maxKm, int take,
        NearbyFilter filter, CancellationToken ct = default)
    {
        if (kind == NearbyKind.StayJob || take < 1) return [];
        var s = ShapeOf(kind);
        var d = Dist(s);
        var alias = kind == NearbyKind.Stay ? "s" : "t";
        var ps = Centre(lat, lng);
        var sql = new StringBuilder($"SELECT {s.IdCol} AS \"Id\", {d}::float8 AS \"DistanceMeters\" FROM {s.From} WHERE {s.Live}");
        sql.Append($" AND ({alias}.\"PriorityRank\" > 0 OR {alias}.\"IsFeatured\") AND ST_DWithin({s.Loc}, {Point}, @maxM)");
        ps.Add(new NpgsqlParameter("maxM", maxKm * 1000.0));
        AddFilters(kind, s, filter, sql, ps);
        sql.Append($" ORDER BY {alias}.\"PriorityRank\" DESC, {alias}.\"IsFeatured\" DESC, {d}, {s.IdCol} LIMIT @take");
        ps.Add(new NpgsqlParameter("take", take));
        return await Run(sql.ToString(), ps, ct);
    }

    private async Task<IReadOnlyList<NearbyRow>> Run(string sql, List<NpgsqlParameter> ps, CancellationToken ct)
    {
        var rows = await context.Database.SqlQueryRaw<Raw>(sql, ps.Cast<object>().ToArray()).ToListAsync(ct);
        return rows.Select(r => new NearbyRow(r.Id, r.DistanceMeters)).ToList();
    }

    // Test hook: the exact SQL text for one call shape.
    internal static string BuildPageSql(NearbyKind kind, bool withAfter, NearbyFilter filter)
    {
        var s = ShapeOf(kind);
        var d = Dist(s);
        var sql = new StringBuilder($"SELECT {s.IdCol} AS \"Id\", {d}::float8 AS \"DistanceMeters\" FROM {s.From} WHERE {s.Live}");
        if (withAfter) sql.Append($" AND ({d} > @afterD OR ({d} = @afterD AND {s.IdCol} > @afterId))");
        AddFilters(kind, s, filter, sql, []);
        sql.Append($" ORDER BY {d}, {s.IdCol} LIMIT @take");
        return sql.ToString();
    }
}

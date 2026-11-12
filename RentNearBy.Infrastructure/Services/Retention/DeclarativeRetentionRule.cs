using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Retention;

// Lives in Infrastructure (not Core) because it names ApplicationDbContext.
public sealed record RetentionContext(ApplicationDbContext Db, DateTime NowUtc, DateTime CutoffUtc, DateOnly TodayIst, DateOnly CutoffDate, int Days);

public interface IRetentionRule
{
    string Name { get; }
    string Group { get; }
    int DefaultDays { get; }
    bool DefaultEnabled { get; }
    bool GuardExempt { get; }
    Task<long> EstimateTableRowsAsync(RetentionContext ctx, CancellationToken ct);
    // Counts candidates, stopping at cap + 1 (a result above cap means over the cap).
    Task<long> CountCandidatesAsync(RetentionContext ctx, long cap, CancellationToken ct);
    // 0 = nothing left.
    Task<int> DeleteBatchAsync(RetentionContext ctx, int batchSize, CancellationToken ct);
}

public interface IRetentionRuleSource { IEnumerable<IRetentionRule> Create(); }

public static class RetentionSqlParams
{
    // Adds only the parameters the statement mentions.
    public static NpgsqlParameter[] For(string sql, RetentionContext ctx, int n)
    {
        var list = new List<NpgsqlParameter>();
        if (Uses(sql, "cutoffDate")) list.Add(new NpgsqlParameter("cutoffDate", NpgsqlDbType.Date) { Value = ctx.CutoffDate });
        if (Uses(sql, "cutoff")) list.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.TimestampTz) { Value = Utc(ctx.CutoffUtc) });
        if (Uses(sql, "today")) list.Add(new NpgsqlParameter("today", NpgsqlDbType.Date) { Value = ctx.TodayIst });
        if (Uses(sql, "now")) list.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = Utc(ctx.NowUtc) });
        if (Uses(sql, "n")) list.Add(new NpgsqlParameter("n", NpgsqlDbType.Integer) { Value = n });
        return list.ToArray();
    }

    private static bool Uses(string sql, string name) => Regex.IsMatch(sql, "@" + name + @"\b");
    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}

public sealed class DeclarativeRetentionRule : IRetentionRule
{
    // Real count(*) when reltuples is stale or the table is small enough to count cheaply.
    public const long ExactCountBelow = 50_000;

    private readonly RetentionRuleDefinition _def;

    public DeclarativeRetentionRule(RetentionRuleDefinition def) { _def = def; }

    public string Name => _def.Name;
    public string Group => _def.Group;
    public int DefaultDays => _def.DefaultDays;
    public bool DefaultEnabled => _def.DefaultEnabled;
    public bool GuardExempt => _def.GuardExempt;
    public RetentionRuleDefinition Definition => _def;

    public static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private string Keys(bool wrap)
    {
        var cols = _def.KeyColumns is { Length: > 0 } ? _def.KeyColumns : new[] { _def.KeyColumn };
        var list = string.Join(", ", cols.Select(c => "t." + Q(c)));
        return wrap && cols.Length > 1 ? "(" + list + ")" : list;
    }

    private string OrderSql => Regex.IsMatch(_def.OrderBy, @"^\w+$") ? "t." + Q(_def.OrderBy) : _def.OrderBy;

    // Alias t, tuple form for composite keys. EligibleSql is wrapped: it may hold a top-level OR.
    public string BuildDeleteSql()
    {
        var table = Q(_def.Table);
        return $"DELETE FROM {table} AS t WHERE {Keys(true)} IN (SELECT {Keys(false)} FROM {table} AS t " +
               $"WHERE ({_def.EligibleSql}) ORDER BY {OrderSql} LIMIT @n FOR UPDATE SKIP LOCKED)";
    }

    public string BuildCountSql(long cap)
        => $"SELECT count(*) AS \"Value\" FROM (SELECT 1 FROM {Q(_def.Table)} AS t WHERE ({_def.EligibleSql}) LIMIT {cap + 1}) x";

    public async Task<long> EstimateTableRowsAsync(RetentionContext ctx, CancellationToken ct)
    {
        var regclass = Q(_def.Table).Replace("'", "''");
        var est = await ctx.Db.Database
            .SqlQueryRaw<long>("SELECT COALESCE((SELECT reltuples::bigint FROM pg_class WHERE oid = to_regclass('" + regclass + "')), -1) AS \"Value\"")
            .SingleAsync(ct);
        if (est >= ExactCountBelow) return est;
        return await ctx.Db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM " + Q(_def.Table)).SingleAsync(ct);
    }

    public async Task<long> CountCandidatesAsync(RetentionContext ctx, long cap, CancellationToken ct)
    {
        var sql = BuildCountSql(cap);
        return await ctx.Db.Database.SqlQueryRaw<long>(sql, RetentionSqlParams.For(sql, ctx, 0).Cast<object>().ToArray()).SingleAsync(ct);
    }

    public async Task<int> DeleteBatchAsync(RetentionContext ctx, int batchSize, CancellationToken ct)
    {
        var sql = BuildDeleteSql();
        return await ctx.Db.Database.ExecuteSqlRawAsync(sql, RetentionSqlParams.For(sql, ctx, batchSize).Cast<object>().ToArray(), ct);
    }
}

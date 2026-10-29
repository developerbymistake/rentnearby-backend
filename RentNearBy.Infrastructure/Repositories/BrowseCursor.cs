using System.Linq.Expressions;
using System.Text;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Infrastructure.Repositories;

// One keyset phase: rows of one PriorityRank, either in the requested city (CityOnly) or the rest of the
// district. Rank OtherRanks is the final catch-all for ranks missing from the catalog (drift before the
// daily reconcile), so no row is ever skipped; it runs as index ranges (OutsideRanks), not a scan.
internal readonly record struct BrowsePhase(short Rank, bool CityOnly);

internal static class BrowseCursor
{
    public const int MaxPage = 10_000;
    public const short OtherRanks = -1;
    private const int MaxLength = 160;
    private const string Version = "v2";

    // Ranks high to low; within a rank the city rows come first when a city is given.
    public static List<BrowsePhase> Phases(IReadOnlyList<short> ranks, bool hasCity)
    {
        var phases = new List<BrowsePhase>(ranks.Count * 2 + 2);
        foreach (var rank in ranks.Distinct().OrderByDescending(r => r))
        {
            if (hasCity) phases.Add(new BrowsePhase(rank, true));
            phases.Add(new BrowsePhase(rank, false));
        }
        phases.Add(new BrowsePhase(OtherRanks, false));
        return phases;
    }

    // Catch-all predicate as index ranges (above the top rank, below the lowest, the gaps between) instead
    // of NOT (rank = ANY(...)), so the drift phase is an index probe that finds ~0 rows, not a district scan.
    public static Expression<Func<T, bool>> OutsideRanks<T>(Expression<Func<T, short>> rankOf, IReadOnlyList<short> ranks)
    {
        var sorted = ranks.Distinct().OrderByDescending(r => r).ToList();
        var param = rankOf.Parameters[0];
        if (sorted.Count == 0) return Expression.Lambda<Func<T, bool>>(Expression.Constant(true), param);

        var rank = Expression.Convert(rankOf.Body, typeof(int));
        Expression Const(short v) => Expression.Constant((int)v);
        Expression body = Expression.OrElse(
            Expression.GreaterThan(rank, Const(sorted[0])),
            Expression.LessThan(rank, Const(sorted[^1])));
        for (var i = 0; i + 1 < sorted.Count; i++)
        {
            if (sorted[i] - sorted[i + 1] <= 1) continue;
            body = Expression.OrElse(body, Expression.AndAlso(
                Expression.LessThan(rank, Const(sorted[i])),
                Expression.GreaterThan(rank, Const(sorted[i + 1]))));
        }
        return Expression.Lambda<Func<T, bool>>(body, param);
    }

    public static string Encode(BrowsePhase phase, DateTime createdAt, Guid id)
    {
        var raw = $"{Version}|{phase.Rank}|{(phase.CityOnly ? 1 : 0)}|{createdAt.Ticks}|{id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // A v1 (phase|ticks|id) or malformed cursor decodes as false: the caller serves the first page.
    public static bool TryDecode(string? cursor, out BrowsePhase phase, out DateTime createdAt, out Guid id)
    {
        phase = default;
        createdAt = default;
        id = default;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxLength) return false;

        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
            if (parts.Length != 5 || parts[0] != Version) return false;
            if (!short.TryParse(parts[1], out var rank) || rank < OtherRanks) return false;
            if (parts[2] is not ("0" or "1")) return false;
            if (!long.TryParse(parts[3], out var ticks) || ticks < 0 || ticks > DateTime.MaxValue.Ticks) return false;
            if (!Guid.TryParseExact(parts[4], "N", out id)) return false;
            phase = new BrowsePhase(rank, parts[2] == "1");
            createdAt = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Resume point inside `phases`; a cursor whose phase no longer exists (catalog changed) restarts.
    public static (int Start, DateTime? AfterAt, Guid AfterId) Resume(string? cursor, List<BrowsePhase> phases)
    {
        if (!TryDecode(cursor, out var phase, out var at, out var id)) return (0, null, Guid.Empty);
        var index = phases.IndexOf(phase);
        return index < 0 ? (0, null, Guid.Empty) : (index, at, id);
    }

    public static BrowsePage<T> OffsetPage<T>(List<T> rows, int pageSize)
    {
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveRange(pageSize, rows.Count - pageSize);
        return new BrowsePage<T>(rows, hasMore, null);
    }

    public static BrowsePage<T> KeysetPage<T>(
        List<T> rows, List<BrowsePhase> rowPhases, int pageSize, Func<T, DateTime> createdAt, Func<T, Guid> id)
    {
        if (rows.Count <= pageSize) return new BrowsePage<T>(rows, false, null);

        rows.RemoveRange(pageSize, rows.Count - pageSize);
        var last = rows[pageSize - 1];
        return new BrowsePage<T>(rows, true, Encode(rowPhases[pageSize - 1], createdAt(last), id(last)));
    }
}

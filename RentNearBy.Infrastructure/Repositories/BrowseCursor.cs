using System.Text;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Infrastructure.Repositories;

internal static class BrowseCursor
{
    public const int MaxPage = 10_000;
    private const int MaxLength = 128;

    public static string Encode(int phase, DateTime createdAt, Guid id)
    {
        var raw = $"{phase}|{createdAt.Ticks}|{id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? cursor, out int phase, out DateTime createdAt, out Guid id)
    {
        phase = 0;
        createdAt = default;
        id = default;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxLength) return false;

        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], out phase) || phase is < 0 or > 1) return false;
            if (!long.TryParse(parts[1], out var ticks) || ticks < 0 || ticks > DateTime.MaxValue.Ticks) return false;
            if (!Guid.TryParseExact(parts[2], "N", out id)) return false;
            createdAt = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static BrowsePage<T> OffsetPage<T>(List<T> rows, int pageSize)
    {
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveRange(pageSize, rows.Count - pageSize);
        return new BrowsePage<T>(rows, hasMore, null);
    }

    public static BrowsePage<T> KeysetPage<T>(
        List<T> rows, int pageSize, int phase0Count, Func<T, DateTime> createdAt, Func<T, Guid> id)
    {
        if (rows.Count <= pageSize) return new BrowsePage<T>(rows, false, null);

        rows.RemoveRange(pageSize, rows.Count - pageSize);
        var last = rows[pageSize - 1];
        var lastPhase = pageSize - 1 < phase0Count ? 0 : 1;
        return new BrowsePage<T>(rows, true, Encode(lastPhase, createdAt(last), id(last)));
    }
}

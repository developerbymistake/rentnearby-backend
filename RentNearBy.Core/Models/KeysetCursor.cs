using System.Text;

namespace RentNearBy.Core.Models;

// Opaque seek cursor over a (timestamp, id) total order: URL-safe base64 of "k1|ticks|id". A list ordered
// by (At DESC, Id DESC) continues with rows strictly after the cursor, so inserts at the head, rows moving
// to the top and rows removed by the caller never shift a later page.
public static class KeysetCursor
{
    private const string Version = "k1";
    private const int MaxLength = 120;

    public static string Encode(DateTime atUtc, Guid id)
    {
        var raw = $"{Version}|{atUtc.Ticks}|{id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string? EncodeLast<T>(IReadOnlyList<T> page, bool hasMore, Func<T, DateTime> at, Func<T, Guid> id)
        => hasMore && page.Count > 0 ? Encode(at(page[^1]), id(page[^1])) : null;

    // A malformed cursor decodes as false.
    public static bool TryDecode(string? cursor, out DateTime atUtc, out Guid id)
    {
        atUtc = default;
        id = default;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxLength) return false;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
            if (parts.Length != 3 || parts[0] != Version) return false;
            if (!long.TryParse(parts[1], out var ticks) || ticks < 0 || ticks > DateTime.MaxValue.Ticks) return false;
            if (!Guid.TryParseExact(parts[2], "N", out id)) return false;
            atUtc = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch (FormatException)
        {
            id = default;
            return false;
        }
    }
}

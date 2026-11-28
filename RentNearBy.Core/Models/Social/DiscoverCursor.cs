using System.Globalization;
using System.Text;

namespace RentNearBy.Core.Models.Social;

// The last examined snapshot row (DESIGN 1.4). Keyset continues below ZScore, recomputed exactly (never a printed double).
public readonly record struct DiscoverCursorValue(long Stamp, int Points, Guid UserId)
{
    public double ZScore => DiscoverRules.ZScore(Points, UserId);
}

// base64url("{snapshotStamp}|{points}|{userId:N}"). Absent or empty = first page; unreadable = 400 Invalid cursor.
public static class DiscoverCursor
{
    public static string Encode(long stamp, int points, Guid userId)
    {
        var raw = string.Create(CultureInfo.InvariantCulture, $"{stamp}|{points}|{userId:N}");
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Encode(DiscoverCursorValue value) => Encode(value.Stamp, value.Points, value.UserId);

    public static bool TryDecode(string? cursor, out DiscoverCursorValue value)
    {
        value = default;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 128) return false;
        string raw;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            raw = Encoding.ASCII.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            return false;
        }
        var p = raw.Split('|');
        if (p.Length != 3) return false;
        if (!long.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var stamp)) return false;
        if (!int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var points) || points > DiscoverRules.MaxPoints) return false;
        if (!Guid.TryParseExact(p[2], "N", out var userId)) return false;
        value = new DiscoverCursorValue(stamp, points, userId);
        return true;
    }
}

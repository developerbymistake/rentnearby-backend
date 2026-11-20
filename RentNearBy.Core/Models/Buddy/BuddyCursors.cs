using System.Globalization;
using System.Text;

namespace RentNearBy.Core.Models.Buddy;

// Opaque cursors KeysetCursor cannot hold: B8 (lower(name), id) and E1 (day, createdAt ticks, id). URL-safe base64.
public static class BuddyCursors
{
    private const string NameVersion = "n1";
    private const string ExpenseVersion = "e1";
    private const int MaxLength = 400;

    public static string EncodeName(string lowerName, Guid id) => Pack($"{NameVersion}|{id:N}|{lowerName}");

    public static bool TryDecodeName(string? cursor, out string lowerName, out Guid id)
    {
        lowerName = string.Empty;
        id = default;
        var raw = Unpack(cursor);
        if (raw is null) return false;
        var parts = raw.Split('|', 3);
        if (parts.Length != 3 || parts[0] != NameVersion || !Guid.TryParseExact(parts[1], "N", out id)) return false;
        lowerName = parts[2];
        return true;
    }

    public static string EncodeExpense(int dayNumber, DateTime createdAtUtc, Guid id)
        => Pack($"{ExpenseVersion}|{dayNumber.ToString(CultureInfo.InvariantCulture)}|{createdAtUtc.Ticks.ToString(CultureInfo.InvariantCulture)}|{id:N}");

    public static bool TryDecodeExpense(string? cursor, out int dayNumber, out DateTime createdAtUtc, out Guid id)
    {
        dayNumber = 0;
        createdAtUtc = default;
        id = default;
        var raw = Unpack(cursor);
        if (raw is null) return false;
        var parts = raw.Split('|');
        if (parts.Length != 4 || parts[0] != ExpenseVersion) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out dayNumber) || dayNumber < 1) return false;
        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks > DateTime.MaxValue.Ticks) return false;
        if (!Guid.TryParseExact(parts[3], "N", out id)) return false;
        createdAtUtc = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    private static string Pack(string raw)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Unpack(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxLength) return null;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

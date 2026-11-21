using System.Globalization;
using System.Text;

namespace RentNearBy.Core.Models.Owner;

// Opaque URL-safe base64 cursors of the owner lists: (date, id) for the calendar and festival lists, (number, id) for the balance-due list.
public static class OwnerCursors
{
    private const string DateVersion = "d1";
    private const string NumberVersion = "i1";
    private const int MaxLength = 200;

    public static string EncodeDate(DateOnly date, Guid id)
        => Pack($"{DateVersion}|{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}|{id:N}");

    public static bool TryDecodeDate(string? cursor, out DateOnly date, out Guid id)
    {
        date = default;
        id = default;
        var parts = Unpack(cursor)?.Split('|');
        return parts is { Length: 3 } && parts[0] == DateVersion
            && DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            && Guid.TryParseExact(parts[2], "N", out id);
    }

    public static string EncodeNumber(long value, Guid id)
        => Pack($"{NumberVersion}|{value.ToString(CultureInfo.InvariantCulture)}|{id:N}");

    public static bool TryDecodeNumber(string? cursor, out long value, out Guid id)
    {
        value = 0;
        id = default;
        var parts = Unpack(cursor)?.Split('|');
        return parts is { Length: 3 } && parts[0] == NumberVersion
            && long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)
            && Guid.TryParseExact(parts[2], "N", out id);
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

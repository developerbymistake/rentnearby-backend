using System.Globalization;

namespace RentNearBy.Core.Models;

// FB-32: keyset cursor of the owner enquiry list, order (CreatedAt DESC, Id DESC). Opaque to the app: "<utc ticks>_<id N>".
public static class OwnerEnquiryCursor
{
    public static string Encode(DateTime createdAtUtc, Guid id)
        => createdAtUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "_" + id.ToString("N");

    public static bool TryDecode(string? cursor, out DateTime createdAtUtc, out Guid id)
    {
        createdAtUtc = default;
        id = default;
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 64) return false;
        var parts = cursor.Split('_');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return false;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || !Guid.TryParseExact(parts[1], "N", out id)) return false;
        createdAtUtc = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }
}

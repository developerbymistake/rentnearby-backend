using System.Globalization;

namespace RentNearBy.Core.Models;

// Generation-stamped cache values: "<generation>|<payload>". A reader gets the value and the current generation in one
// MGET; another stamp (or an old unstamped value) is a miss. A change bumps the generation with one INCR, so no keyspace
// SCAN runs in the request and old entries just expire by TTL. A value built while a bump happened keeps the old stamp
// and is never served.
public static class CacheStamp
{
    public static long Generation(string? raw)
        => long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) ? generation : 0;

    public static string Wrap(long generation, string payload)
        => string.Create(CultureInfo.InvariantCulture, $"{generation}|{payload}");

    public static string? Unwrap(string? raw, long generation)
    {
        if (raw is null) return null;
        var bar = raw.IndexOf('|');
        if (bar <= 0) return null;
        if (!long.TryParse(raw.AsSpan(0, bar), NumberStyles.None, CultureInfo.InvariantCulture, out var stamp) || stamp != generation)
            return null;
        return raw[(bar + 1)..];
    }
}

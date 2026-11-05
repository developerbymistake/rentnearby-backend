namespace RentNearBy.Core.Models;

// A room/plot is public while live and its paid/free window has not ended; the hourly sweep only does side effects.
public static class ListingVisibility
{
    public static bool IsPublic(bool isActive, bool isDeleted, DateTime? validUntil, DateTime nowUtc)
        => isActive && !isDeleted && (validUntil == null || validUntil > nowUtc);

    // A cached public list may live only until its first row expires; null = do not cache.
    public static TimeSpan? CacheTtl(TimeSpan ttl, DateTime nowUtc, IEnumerable<DateTime?> validUntils)
    {
        DateTime? first = null;
        foreach (var v in validUntils)
            if (v is DateTime d && (first == null || d < first)) first = d;
        if (first is not DateTime end) return ttl;
        var left = end - nowUtc;
        if (left <= TimeSpan.Zero) return null;
        return left < ttl ? left : ttl;
    }
}

// PublishedAt = when the row last entered the public set from a state with no running live window
// (first approval, renewal after expiry or report takedown). A resume inside a running window keeps it.
public static class ListingPublishRules
{
    public static DateTime PublishedAtOnActivation(DateTime current, DateTime? validUntilBefore, DateTime nowUtc)
        => validUntilBefore is DateTime v && v > nowUtc ? current : nowUtc;
}

// One total order for every "recent" list: paid rank first, then newest go-live, then id (uuid byte order,
// the same as the lowercase "D" string order). Server truncation and any client merge must both use it.
public static class ListingFeedOrder
{
    public readonly record struct Key(short Rank, DateTime PublishedAt, Guid Id);

    public static int Compare(Key a, Key b)
    {
        var c = b.Rank.CompareTo(a.Rank);
        if (c != 0) return c;
        c = b.PublishedAt.CompareTo(a.PublishedAt);
        if (c != 0) return c;
        return string.CompareOrdinal(b.Id.ToString("D"), a.Id.ToString("D"));
    }

    // k-way merge of lists each sorted by Compare; exact top `take` of the union when each list holds its own top `take`.
    public static List<T> MergeTop<T>(IEnumerable<IReadOnlyList<T>> lists, Func<T, Key> keyOf, int take)
    {
        var result = new List<T>();
        if (take <= 0) return result;
        var sources = lists.Where(l => l.Count > 0).ToList();
        var heads = new int[sources.Count];
        var queue = new PriorityQueue<int, Key>(Comparer<Key>.Create(Compare));
        for (var s = 0; s < sources.Count; s++) queue.Enqueue(s, keyOf(sources[s][0]));

        while (result.Count < take && queue.TryDequeue(out var s, out _))
        {
            result.Add(sources[s][heads[s]]);
            if (++heads[s] < sources[s].Count) queue.Enqueue(s, keyOf(sources[s][heads[s]]));
        }
        return result;
    }
}

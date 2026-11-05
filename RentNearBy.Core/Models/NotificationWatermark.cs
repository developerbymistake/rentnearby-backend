namespace RentNearBy.Core.Models;

// "Mark all read" without hiding a row that commits after the tap. A row's CreatedAt is taken before its insert commits,
// so a watermark of "now" can cover a row the tap never saw. The tap therefore sets the watermark SettleSeconds back
// (database clock, same statement) and marks each row it can see newer than that with its own NotificationRead.
// A row still in flight at the tap is newer than the watermark and has no read row, so it stays unread; this holds as
// long as building and committing a row takes less than SettleSeconds. NotificationRepository.MarkAllReadAsync runs it in SQL.
public static class NotificationWatermark
{
    public const int SettleSeconds = 60;

    public static DateTime WatermarkAt(DateTime dbNowUtc) => dbNowUtc.AddSeconds(-SettleSeconds);

    public static (DateTime Watermark, IReadOnlyList<Guid> ReadIds) MarkAll(
        DateTime dbNowUtc, IEnumerable<(Guid Id, DateTime CreatedAt)> visibleRows)
    {
        var watermark = WatermarkAt(dbNowUtc);
        var ids = visibleRows.Where(r => r.CreatedAt > watermark).Select(r => r.Id).Distinct().ToList();
        return (watermark, ids);
    }

    public static bool IsRead(DateTime createdAt, DateTime? readUpTo, bool hasReadRow)
        => hasReadRow || (readUpTo is { } w && createdAt <= w);
}

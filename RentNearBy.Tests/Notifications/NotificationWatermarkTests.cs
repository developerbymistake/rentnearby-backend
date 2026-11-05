namespace RentNearBy.Tests.Notifications;

public class NotificationWatermarkTests
{
    private static readonly DateTime T10 = new(2026, 11, 5, 4, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void RowBuiltBeforeTapAndCommittedAfter_StaysUnread()
    {
        var builtAt = T10.AddMilliseconds(100);
        var tapAt = T10.AddMilliseconds(300);
        var older = (Id: Guid.NewGuid(), CreatedAt: T10.AddMinutes(-5));
        var recent = (Id: Guid.NewGuid(), CreatedAt: T10.AddMilliseconds(50));

        var (watermark, readIds) = NotificationWatermark.MarkAll(tapAt, [older, recent]);

        Assert.True(NotificationWatermark.IsRead(older.CreatedAt, watermark, readIds.Contains(older.Id)));
        Assert.True(NotificationWatermark.IsRead(recent.CreatedAt, watermark, readIds.Contains(recent.Id)));
        Assert.False(NotificationWatermark.IsRead(builtAt, watermark, hasReadRow: false));
    }

    [Fact]
    public void Watermark_IsSettleSecondsBehindDbNow()
        => Assert.Equal(T10.AddSeconds(-60), NotificationWatermark.WatermarkAt(T10));

    [Fact]
    public void MarkAll_OnlyTailGetsReadRows_Deduped()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var (_, readIds) = NotificationWatermark.MarkAll(T10,
        [
            (a, T10.AddSeconds(-10)),
            (a, T10.AddSeconds(-10)),
            (b, T10.AddSeconds(-60)),
            (Guid.NewGuid(), T10.AddHours(-2)),
        ]);

        Assert.Equal(new[] { a }, readIds);
    }

    [Fact]
    public void MarkAll_EmptyInbox()
    {
        var (watermark, readIds) = NotificationWatermark.MarkAll(T10, []);
        Assert.Empty(readIds);
        Assert.Equal(T10.AddSeconds(-60), watermark);
    }

    [Fact]
    public void IsRead_NoWatermarkNoRow_Unread()
        => Assert.False(NotificationWatermark.IsRead(T10, null, false));
}

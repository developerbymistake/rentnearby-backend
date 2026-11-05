namespace RentNearBy.Core.Models;

// Read state = per-row NotificationRead OR CreatedAt <= the user's read watermark (Users."NotificationsReadUpTo",
// a shadow property): "mark all read" moves the watermark (never back), unread counts only the tail after it.
public static class NotificationInbox
{
    public const string ReadUpToProperty = "NotificationsReadUpTo";
    public const int RetentionDays = 180, PurgeBatch = 1000;
}

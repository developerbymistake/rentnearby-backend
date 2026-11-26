using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

// Pairs an event with whether the current caller has read it — computed via a per-row EXISTS
// against NotificationRead, never a stored column (see NotificationRead's own doc comment on why
// read-state is a lazy join, not a fan-out column).
public record NotificationListItem(NotificationEvent Notification, bool IsRead);

public interface INotificationRepository : IRepository<NotificationEvent>
{
    // WHERE TargetUserId = userId only, for now — broadcast (TargetUserId IS NULL) support is an
    // OR-branch to add here once a broadcast producer actually exists; no schema change needed.
    // seeker = false (the account is no longer a seeker): rows of TravellerOnly.NotificationTypes are left out, never deleted.
    Task<(IReadOnlyList<NotificationListItem> Items, bool HasMore)> GetPagedForUserAsync(
        Guid userId, bool seeker, int page, int pageSize);

    Task<(IReadOnlyList<NotificationListItem> Items, bool HasMore)> GetForUserAfterCursorAsync(
        Guid userId, bool seeker, DateTime? afterAt, Guid? afterId, int pageSize);

    Task<int> GetUnreadCountAsync(Guid userId, bool seeker);

    // Idempotent upsert (ON CONFLICT DO NOTHING) — ownership is enforced in the same statement's
    // WHERE clause, never a separate lookup. Returns rows affected (0 if the id doesn't exist, isn't
    // targeted at this user, or was already read — all three are indistinguishable by design).
    Task<int> MarkReadAsync(Guid notificationId, Guid userId);

    // One UPDATE of the user's read watermark (NotificationInbox), whatever the inbox size. seeker = false: only the visible
    // rows after the watermark get a read row and the watermark stays, so the hidden traveller rows keep their read state.
    Task<int> MarkAllReadAsync(Guid userId, bool seeker);

    // Deletes the user's other rows of this type whose ActionArgumentsJson carries enquiryId (one inbox row per alert).
    Task<int> DeleteSupersededAsync(Guid userId, string type, Guid enquiryId, Guid keepId);

    // Deletes at most `batch` rows created before cutoffUtc, oldest first. Returns rows deleted.
    Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, int batch);

    Task<int> DeleteByActionRouteForUserAsync(Guid userId, string actionRoute);
}

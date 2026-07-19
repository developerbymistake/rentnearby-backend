using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

// Pairs an event with whether the current caller has read it — computed via a per-row EXISTS
// against NotificationRead, never a stored column (see NotificationRead's own doc comment on why
// read-state is a lazy join, not a fan-out column).
public record NotificationListItem(NotificationEvent Notification, bool IsRead);

// Admin's system-wide feed row — no per-admin read state (see AdminNotificationDto's doc comment),
// but resolves which Agent a lead-assignment row actually targeted, since Title/Body are written
// from the Agent's own POV and don't name them.
public record AdminNotificationListItem(NotificationEvent Notification, string? TargetAgentName);

public interface INotificationRepository : IRepository<NotificationEvent>
{
    // WHERE TargetUserId = userId only, for now — broadcast (TargetUserId IS NULL) support is an
    // OR-branch to add here once a broadcast producer actually exists; no schema change needed.
    Task<(IReadOnlyList<NotificationListItem> Items, bool HasMore)> GetPagedForUserAsync(
        Guid userId, int page, int pageSize);

    // No TargetUserId filter — the same NotificationEvent table read system-wide, so Admin sees
    // every notification any Agent got, with zero new storage. See the plan's design decisions.
    Task<(IReadOnlyList<AdminNotificationListItem> Items, bool HasMore)> GetPagedForAdminAsync(
        int page, int pageSize);

    Task<int> GetUnreadCountAsync(Guid userId);

    // Idempotent upsert (ON CONFLICT DO NOTHING) — ownership is enforced in the same statement's
    // WHERE clause, never a separate lookup. Returns rows affected (0 if the id doesn't exist, isn't
    // targeted at this user, or was already read — all three are indistinguishable by design).
    Task<int> MarkReadAsync(Guid notificationId, Guid userId);

    // Single bulk INSERT ... SELECT ... ON CONFLICT DO NOTHING — not a per-row loop.
    Task<int> MarkAllReadAsync(Guid userId);
}

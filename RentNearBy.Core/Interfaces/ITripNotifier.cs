using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface ITripNotifier
{
    // Inbox row (own SaveChanges), hub NotificationReceived, notification.push. Best effort, never throws.
    Task SendAsync(NotificationEvent n);
    // TripPushLog insert first; false = duplicate key or failed, nothing sent.
    Task<bool> SendOnceAsync(NotificationEvent n, string dedupeKey, Guid? tripId, Guid? enquiryId);
    // Same send; tells a duplicate key (already sent) from a failure (nothing committed, retry the same key).
    Task<TripSendOutcome> TrySendOnceAsync(NotificationEvent n, string dedupeKey, Guid? tripId, Guid? enquiryId);
}

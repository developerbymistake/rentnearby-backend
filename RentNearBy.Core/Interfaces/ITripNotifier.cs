using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface ITripNotifier
{
    // Inbox row (own SaveChanges), hub NotificationReceived, notification.push. Best effort, never throws.
    Task SendAsync(NotificationEvent n);
    // Hub NotificationReceived only (the row and the push are already committed through the outbox). Best effort, never throws.
    Task PublishLiveAsync(IEnumerable<NotificationEvent> events);
    // TripPushLog insert first; false = duplicate key or failed, nothing sent.
    Task<bool> SendOnceAsync(NotificationEvent n, string dedupeKey, Guid? tripId, Guid? enquiryId);
    // Same send; tells a duplicate key (already sent) from a failure (nothing committed, retry the same key).
    Task<TripSendOutcome> TrySendOnceAsync(NotificationEvent n, string dedupeKey, Guid? tripId, Guid? enquiryId);
}

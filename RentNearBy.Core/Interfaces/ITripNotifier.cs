using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ITripNotifier
{
    // Inbox row (own SaveChanges), hub NotificationReceived, notification.push. Best effort, never throws.
    Task SendAsync(NotificationEvent n);
    // TripPushLog insert first; false = duplicate key, nothing sent.
    Task<bool> SendOnceAsync(NotificationEvent n, string dedupeKey, Guid? tripId, Guid? enquiryId);
}

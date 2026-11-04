using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Api.Services;

public sealed class TripNotifier(
    IUnitOfWork unitOfWork, ApplicationDbContext db, IHubContext<NotificationHub> hub,
    IRabbitMqPublisher publisher, ILogger<TripNotifier> logger) : ITripNotifier
{
    public async Task SendAsync(NotificationEvent n)
    {
        if (n.TargetUserId is null) return;

        try
        {
            await unitOfWork.Notifications.AddAsync(n);
            await unitOfWork.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "trip.notify.save_failed {Type}", n.Type);
            db.Entry(n).State = EntityState.Detached;
            return;
        }

        await AdminHandlers.SendGoLiveNotificationAsync(hub, publisher, n);
    }

    public async Task<bool> SendOnceAsync(NotificationEvent n, string dedupeKey, Guid? tripId, Guid? enquiryId)
    {
        bool inserted;
        try
        {
            inserted = await unitOfWork.TripPushLogs.TryInsertAsync(new TripPushLog
            {
                Id = Guid.NewGuid(),
                TripId = tripId,
                TripEnquiryId = enquiryId,
                DedupeKey = dedupeKey,
                Kind = n.Type,
                SentAt = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "trip.notify.log_failed {Key}", dedupeKey);
            return false;
        }

        if (!inserted) return false;
        await SendAsync(n);
        return true;
    }
}

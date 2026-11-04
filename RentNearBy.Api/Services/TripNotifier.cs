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

    // The dedupe row and the inbox row commit together, so a failed inbox save leaves the key free for the next pass.
    public async Task<bool> SendOnceAsync(NotificationEvent n, string dedupeKey, Guid? tripId, Guid? enquiryId)
    {
        var tx = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync() : null;
        try
        {
            var inserted = await unitOfWork.TripPushLogs.TryInsertAsync(new TripPushLog
            {
                Id = Guid.NewGuid(),
                TripId = tripId,
                TripEnquiryId = enquiryId,
                DedupeKey = dedupeKey,
                Kind = n.Type,
                SentAt = DateTime.UtcNow,
            });
            if (!inserted)
            {
                if (tx != null) await tx.RollbackAsync();
                return false;
            }

            if (n.TargetUserId != null)
            {
                await unitOfWork.Notifications.AddAsync(n);
                await unitOfWork.SaveChangesAsync();
            }
            if (tx != null) await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "trip.notify.once_failed {Key} {Type}", dedupeKey, n.Type);
            if (tx != null)
            {
                try { await tx.RollbackAsync(); } catch { }
            }
            db.Entry(n).State = EntityState.Detached;
            return false;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }

        if (n.TargetUserId != null) await AdminHandlers.SendGoLiveNotificationAsync(hub, publisher, n);
        return true;
    }
}

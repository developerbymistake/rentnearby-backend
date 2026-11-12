using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Api.Services;

public sealed class TripNotifier(
    IUnitOfWork unitOfWork, ApplicationDbContext db, IHubContext<NotificationHub> hub,
    IOutboxWriter outbox, ILogger<TripNotifier> logger) : ITripNotifier
{
    // The push is an outbox row written with the inbox row (one SaveChanges), so a crash or a broker outage never loses it.
    private void QueuePush(NotificationEvent n)
        => outbox.AddPush(new OutboxPushPayload(
            $"ev:{n.Id:N}", n.TargetUserId!.Value, n.Id, n.Title, n.Body, n.ActionRoute, n.ActionArgumentsJson, $"n:{n.Id:N}"), DateTime.UtcNow);

    private async Task AfterCommitAsync(NotificationEvent n)
    {
        outbox.Wake();
        await HubAsync(n);
    }

    public async Task PublishLiveAsync(IEnumerable<NotificationEvent> events)
    {
        foreach (var n in events) await HubAsync(n);
    }

    private async Task HubAsync(NotificationEvent n)
    {
        try
        {
            await hub.Clients.Group($"user_{n.TargetUserId}").SendAsync("NotificationReceived", new
            {
                id = n.Id,
                type = n.Type,
                title = n.Title,
                body = n.Body,
                actionRoute = n.ActionRoute,
                actionArguments = NotificationDto.ParseArguments(n.ActionArgumentsJson),
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "notify.hub_failed {NotificationId} {Type}", n.Id, n.Type);
        }
    }

    public async Task SendAsync(NotificationEvent n)
    {
        if (n.TargetUserId is null) return;

        try
        {
            await unitOfWork.Notifications.AddAsync(n);
            QueuePush(n);
            await unitOfWork.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "trip.notify.save_failed {Type}", n.Type);
            db.Entry(n).State = EntityState.Detached;
            return;
        }

        await AfterCommitAsync(n);
    }

    public async Task<bool> SendOnceAsync(NotificationEvent n, string dedupeKey, Guid? tripId, Guid? enquiryId)
        => await TrySendOnceAsync(n, dedupeKey, tripId, enquiryId) == TripSendOutcome.Sent;

    // The dedupe row and the inbox row commit together, so a failed inbox save leaves the key free for the next pass.
    public async Task<TripSendOutcome> TrySendOnceAsync(NotificationEvent n, string dedupeKey, Guid? tripId, Guid? enquiryId)
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
                return TripSendOutcome.Duplicate;
            }

            if (n.TargetUserId != null)
            {
                await unitOfWork.Notifications.AddAsync(n);
                QueuePush(n);
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
            return TripSendOutcome.Failed;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }

        if (n.TargetUserId != null) await AfterCommitAsync(n);
        return TripSendOutcome.Sent;
    }
}

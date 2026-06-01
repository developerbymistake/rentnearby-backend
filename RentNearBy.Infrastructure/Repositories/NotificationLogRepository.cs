using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class NotificationLogRepository(ApplicationDbContext context) : INotificationLogRepository
{
    public async Task AddAsync(NotificationLog log)
        => await context.NotificationLogs.AddAsync(log);

    public async Task<bool> WasSentTodayAsync(Guid userId, string type)
    {
        var todayUtc = DateTime.UtcNow.Date;
        return await context.NotificationLogs
            .AnyAsync(n => n.UserId == userId
                        && n.Type == type
                        && n.IsSuccess
                        && n.SentAt >= todayUtc
                        && n.SentAt < todayUtc.AddDays(1));
    }
}

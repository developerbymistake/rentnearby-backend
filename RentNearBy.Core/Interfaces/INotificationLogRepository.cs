using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface INotificationLogRepository
{
    Task AddAsync(NotificationLog log);
    Task<bool> WasSentTodayAsync(Guid userId, string type);
}

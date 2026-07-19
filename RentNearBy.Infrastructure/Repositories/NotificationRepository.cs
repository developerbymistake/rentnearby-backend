using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class NotificationRepository(ApplicationDbContext context)
    : Repository<NotificationEvent>(context), INotificationRepository
{
    public async Task<(IReadOnlyList<NotificationListItem> Items, bool HasMore)> GetPagedForUserAsync(
        Guid userId, int page, int pageSize)
    {
        var take = pageSize + 1;
        var items = await _context.NotificationEvents.AsNoTracking()
            .Where(n => n.TargetUserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(take)
            .Select(n => new NotificationListItem(
                n,
                _context.NotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == userId)))
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }

    public async Task<(IReadOnlyList<AdminNotificationListItem> Items, bool HasMore)> GetPagedForAdminAsync(
        int page, int pageSize)
    {
        var take = pageSize + 1;
        var items = await (
            from n in _context.NotificationEvents.AsNoTracking()
            join a in _context.Agents.AsNoTracking() on n.TargetUserId equals a.UserId into agentJoin
            from agent in agentJoin.DefaultIfEmpty()
            orderby n.CreatedAt descending
            select new AdminNotificationListItem(n, agent != null ? agent.Name : null))
            .Skip((page - 1) * pageSize)
            .Take(take)
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }

    public async Task<int> GetUnreadCountAsync(Guid userId)
        => await _context.NotificationEvents.AsNoTracking()
            .CountAsync(n => n.TargetUserId == userId
                && !_context.NotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == userId));

    public async Task<int> MarkReadAsync(Guid notificationId, Guid userId)
        => await _context.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""NotificationReads"" (""UserId"", ""NotificationId"", ""ReadAt"")
            SELECT {userId}, ""Id"", now()
            FROM ""NotificationEvents""
            WHERE ""Id"" = {notificationId} AND (""TargetUserId"" = {userId} OR ""TargetUserId"" IS NULL)
            ON CONFLICT (""UserId"", ""NotificationId"") DO NOTHING");

    public async Task<int> MarkAllReadAsync(Guid userId)
        => await _context.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""NotificationReads"" (""UserId"", ""NotificationId"", ""ReadAt"")
            SELECT {userId}, n.""Id"", now()
            FROM ""NotificationEvents"" n
            LEFT JOIN ""NotificationReads"" r ON r.""NotificationId"" = n.""Id"" AND r.""UserId"" = {userId}
            WHERE n.""TargetUserId"" = {userId} AND r.""NotificationId"" IS NULL
            ON CONFLICT (""UserId"", ""NotificationId"") DO NOTHING");
}

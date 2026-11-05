using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class NotificationRepository(ApplicationDbContext context)
    : Repository<NotificationEvent>(context), INotificationRepository
{
    public Task<(IReadOnlyList<NotificationListItem> Items, bool HasMore)> GetPagedForUserAsync(
        Guid userId, int page, int pageSize)
        => PageAsync(_context.NotificationEvents.AsNoTracking().Where(n => n.TargetUserId == userId),
            userId, (page - 1) * pageSize, pageSize);

    // Seek page on (CreatedAt DESC, Id DESC): rows strictly after the cursor, so new rows at the head never
    // repeat a row on a later page.
    public Task<(IReadOnlyList<NotificationListItem> Items, bool HasMore)> GetForUserAfterCursorAsync(
        Guid userId, DateTime? afterAt, Guid? afterId, int pageSize)
    {
        var query = _context.NotificationEvents.AsNoTracking().Where(n => n.TargetUserId == userId);
        if (afterAt is { } at && afterId is { } id)
            query = query.Where(n => n.CreatedAt <= at && (n.CreatedAt < at || n.Id.CompareTo(id) < 0));
        return PageAsync(query, userId, 0, pageSize);
    }

    private async Task<(IReadOnlyList<NotificationListItem> Items, bool HasMore)> PageAsync(
        IQueryable<NotificationEvent> query, Guid userId, int skip, int pageSize)
    {
        var take = pageSize + 1;
        var readUpTo = await ReadUpToAsync(userId);
        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Skip(skip)
            .Take(take)
            .Select(n => new NotificationListItem(
                n,
                (readUpTo != null && n.CreatedAt <= readUpTo)
                || _context.NotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == userId)))
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }

    // Range scan of (TargetUserId, CreatedAt) bounded to the tail after the watermark, not every row ever sent.
    public async Task<int> GetUnreadCountAsync(Guid userId)
    {
        var readUpTo = await ReadUpToAsync(userId);
        var query = _context.NotificationEvents.AsNoTracking().Where(n => n.TargetUserId == userId);
        if (readUpTo is { } w) query = query.Where(n => n.CreatedAt > w);
        return await query.CountAsync(n => !_context.NotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == userId));
    }

    private async Task<DateTime?> ReadUpToAsync(Guid userId)
        => await _context.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => EF.Property<DateTime?>(u, NotificationInbox.ReadUpToProperty))
            .FirstOrDefaultAsync();

    public async Task<int> MarkReadAsync(Guid notificationId, Guid userId)
        => await _context.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""NotificationReads"" (""UserId"", ""NotificationId"", ""ReadAt"")
            SELECT {userId}, ""Id"", now()
            FROM ""NotificationEvents""
            WHERE ""Id"" = {notificationId} AND (""TargetUserId"" = {userId} OR ""TargetUserId"" IS NULL)
            ON CONFLICT (""UserId"", ""NotificationId"") DO NOTHING");

    // One row update: the watermark moves to now (never back), whatever the inbox size.
    public async Task<int> MarkAllReadAsync(Guid userId)
    {
        var now = DateTime.UtcNow;
        return await _context.Users
            .Where(u => u.Id == userId
                && (EF.Property<DateTime?>(u, NotificationInbox.ReadUpToProperty) == null
                    || EF.Property<DateTime?>(u, NotificationInbox.ReadUpToProperty) < now))
            .ExecuteUpdateAsync(s => s.SetProperty(u => EF.Property<DateTime?>(u, NotificationInbox.ReadUpToProperty), now));
    }

    // A repeating alert keeps one inbox row: older rows of this type for this enquiry go (their reads cascade).
    public async Task<int> DeleteSupersededAsync(Guid userId, string type, Guid enquiryId, Guid keepId)
    {
        var probe = JsonSerializer.Serialize(new { enquiryId });
        return await _context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "NotificationEvents"
            WHERE "TargetUserId" = {userId} AND "Type" = {type} AND "Id" <> {keepId}
              AND "ActionArgumentsJson" @> {probe}::jsonb
            """);
    }

    // Retention: at most `batch` of the oldest rows per call (ix_notificationevents_created); reads cascade.
    public async Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, int batch)
        => await _context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "NotificationEvents"
            WHERE "Id" IN (SELECT "Id" FROM "NotificationEvents" WHERE "CreatedAt" < {cutoffUtc} ORDER BY "CreatedAt" LIMIT {batch})
            """);

    // NotificationRead rows go with their event via the cascade FK.
    public async Task<int> DeleteByActionRouteForUserAsync(Guid userId, string actionRoute)
        => await _context.NotificationEvents
            .Where(n => n.TargetUserId == userId && n.ActionRoute == actionRoute)
            .ExecuteDeleteAsync();
}

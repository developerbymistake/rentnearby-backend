using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Blocking = live with paid time left, or a pending Go Live request. Stays and taxis: under review or approved.
public class RoleChangeGuardService(ApplicationDbContext context) : IRoleChangeGuardService
{
    public async Task<BlockingListingCounts> GetBlockingCountsAsync(Guid userId)
    {
        var now = DateTime.UtcNow;

        var rooms = await context.RoomListings.AsNoTracking()
            .CountAsync(l => l.UserId == userId && !l.IsDeleted
                && ((l.IsActive && l.ValidUntil != null && l.ValidUntil > now)
                    || l.LiveRequestStatus == GoLiveRequestStatuses.Pending));

        var plots = await context.PlotListings.AsNoTracking()
            .CountAsync(l => l.UserId == userId && !l.IsDeleted
                && ((l.IsActive && l.ValidUntil != null && l.ValidUntil > now)
                    || l.LiveRequestStatus == GoLiveRequestStatuses.Pending));

        var stays = await context.StayListings.AsNoTracking()
            .CountAsync(s => s.UserId == userId && !s.IsDeleted
                && (s.Status == StayStatuses.PendingReview || s.Status == StayStatuses.Approved));

        var taxis = await context.TaxiListings.AsNoTracking()
            .CountAsync(t => t.UserId == userId && !t.IsDeleted
                && (t.Status == TaxiStatuses.PendingReview || t.Status == TaxiStatuses.Approved));

        return new BlockingListingCounts(rooms, plots, stays, taxis);
    }

    public async Task<IReadOnlyDictionary<Guid, BlockingListingCounts>> GetBlockingCountsAsync(
        IReadOnlyCollection<Guid> userIds)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, BlockingListingCounts>();

        var now = DateTime.UtcNow;

        var rooms = await context.RoomListings.AsNoTracking()
            .Where(l => userIds.Contains(l.UserId) && !l.IsDeleted
                && ((l.IsActive && l.ValidUntil != null && l.ValidUntil > now)
                    || l.LiveRequestStatus == GoLiveRequestStatuses.Pending))
            .GroupBy(l => l.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count);

        var plots = await context.PlotListings.AsNoTracking()
            .Where(l => userIds.Contains(l.UserId) && !l.IsDeleted
                && ((l.IsActive && l.ValidUntil != null && l.ValidUntil > now)
                    || l.LiveRequestStatus == GoLiveRequestStatuses.Pending))
            .GroupBy(l => l.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count);

        var stays = await context.StayListings.AsNoTracking()
            .Where(s => userIds.Contains(s.UserId) && !s.IsDeleted
                && (s.Status == StayStatuses.PendingReview || s.Status == StayStatuses.Approved))
            .GroupBy(s => s.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count);

        var taxis = await context.TaxiListings.AsNoTracking()
            .Where(t => userIds.Contains(t.UserId) && !t.IsDeleted
                && (t.Status == TaxiStatuses.PendingReview || t.Status == TaxiStatuses.Approved))
            .GroupBy(t => t.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count);

        return userIds.Distinct().ToDictionary(
            id => id,
            id => new BlockingListingCounts(
                rooms.GetValueOrDefault(id), plots.GetValueOrDefault(id), stays.GetValueOrDefault(id),
                taxis.GetValueOrDefault(id)));
    }
}

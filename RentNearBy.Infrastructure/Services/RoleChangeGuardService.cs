using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Blocking = live with paid time left, or a pending Go Live request. Stays: a stay under review.
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
            .CountAsync(s => s.UserId == userId && !s.IsDeleted && s.Status == StayStatuses.PendingReview);

        return new BlockingListingCounts(rooms, plots, stays);
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
            .Where(s => userIds.Contains(s.UserId) && !s.IsDeleted && s.Status == StayStatuses.PendingReview)
            .GroupBy(s => s.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count);

        return userIds.Distinct().ToDictionary(
            id => id,
            id => new BlockingListingCounts(
                rooms.GetValueOrDefault(id), plots.GetValueOrDefault(id), stays.GetValueOrDefault(id)));
    }
}

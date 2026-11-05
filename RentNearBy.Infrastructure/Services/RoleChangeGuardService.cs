using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Blocking = a stay or taxi under review, approved, or ever approved (even if later rejected or deleted),
// whatever the tab state. The batch overload counts live rows only (display).
public class RoleChangeGuardService(ApplicationDbContext context)
    : IRoleChangeGuardService
{
    public async Task<int> GetBlockingCountAsync(Guid userId, string leavingKind)
    {
        return leavingKind switch
        {
            ListingKinds.Stay => await context.StayListings.AsNoTracking()
                .CountAsync(s => s.UserId == userId
                    && (s.EverApproved
                        || (!s.IsDeleted && (s.Status == StayStatuses.PendingReview || s.Status == StayStatuses.Approved)))),
            ListingKinds.Taxi => await context.TaxiListings.AsNoTracking()
                .CountAsync(t => t.UserId == userId
                    && (t.EverApproved
                        || (!t.IsDeleted && (t.Status == TaxiStatuses.PendingReview || t.Status == TaxiStatuses.Approved)))),
            _ => 0,
        };
    }

    public async Task<IReadOnlyDictionary<Guid, BlockingListingCounts>> GetBlockingCountsAsync(
        IReadOnlyCollection<Guid> userIds)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, BlockingListingCounts>();

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
            id => new BlockingListingCounts(stays.GetValueOrDefault(id), taxis.GetValueOrDefault(id)));
    }
}

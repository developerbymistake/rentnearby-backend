using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Extensions;

namespace RentNearBy.Infrastructure.Repositories;

public class ListingReportRepository(ApplicationDbContext context) : IListingReportRepository
{
    public async Task AddAsync(ListingReport report)
        => await context.ListingReports.AddAsync(report);

    public async Task<bool> HasPendingReportForListingAsync(Guid listingId, string listingType)
        => await context.ListingReports.AnyAsync(r =>
            r.ListingId == listingId && r.ListingType == listingType && r.Status == "Pending");

    public async Task<bool> HasPendingReportFromReporterAsync(Guid listingId, string listingType, Guid reporterUserId)
        => await context.ListingReports.AnyAsync(r =>
            r.ListingId == listingId && r.ListingType == listingType &&
            r.ReporterUserId == reporterUserId && r.Status == "Pending");

    public async Task<ListingReport?> GetByIdAsync(Guid id)
        => await context.ListingReports
            .AsNoTracking()
            .Include(r => r.Reason)
            .FirstOrDefaultAsync(r => r.Id == id);

    public async Task<PagedResult<ListingReport>> GetPagedAsync(int page, int pageSize, string? status)
    {
        var query = context.ListingReports
            .AsNoTracking()
            .Include(r => r.Reason)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("All", StringComparison.OrdinalIgnoreCase))
            query = query.Where(r => r.Status == status);

        return await query.OrderByDescending(r => r.CreatedAt).ToPagedResultAsync(page, pageSize);
    }

    public async Task<int> AutoResolvePendingForListingAsync(Guid listingId, string listingType)
    {
        return await context.ListingReports
            .Where(r => r.ListingId == listingId && r.ListingType == listingType && r.Status == "Pending")
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "Resolved")
                .SetProperty(r => r.ResolutionAction, "AutoResolvedByOwner")
                .SetProperty(r => r.ResolvedAt, DateTime.UtcNow));
    }

    public async Task ResolveAsync(Guid reportId, Guid? adminId, string resolutionAction)
    {
        await context.ListingReports
            .Where(r => r.Id == reportId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "Resolved")
                .SetProperty(r => r.ResolutionAction, resolutionAction)
                .SetProperty(r => r.ResolvedAt, DateTime.UtcNow)
                .SetProperty(r => r.ResolvedByAdminId, adminId));
    }

    public async Task<PagedResult<ListingReport>> GetPagedForListingAsync(Guid listingId, string listingType, int page, int pageSize)
        => await context.ListingReports
            .AsNoTracking()
            .Include(r => r.Reason)
            .Where(r => r.ListingId == listingId && r.ListingType == listingType)
            .OrderByDescending(r => r.CreatedAt)
            .ToPagedResultAsync(page, pageSize);

    public async Task<PagedResult<ListingReport>> GetPagedForReporterAsync(Guid reporterUserId, int page, int pageSize)
        => await context.ListingReports
            .AsNoTracking()
            .Include(r => r.Reason)
            .Where(r => r.ReporterUserId == reporterUserId)
            .OrderByDescending(r => r.CreatedAt)
            .ToPagedResultAsync(page, pageSize);

    public async Task<Dictionary<Guid, int>> GetPendingCountsForListingsAsync(IEnumerable<Guid> listingIds, string listingType)
    {
        var ids = listingIds.ToList();
        if (ids.Count == 0) return new();
        return await context.ListingReports
            .AsNoTracking()
            .Where(r => ids.Contains(r.ListingId) && r.ListingType == listingType && r.Status == "Pending")
            .GroupBy(r => r.ListingId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }
}

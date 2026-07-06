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
}

using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class InquiryRepository(ApplicationDbContext context)
    : Repository<Inquiry>(context), IInquiryRepository
{
    // "Live" = not yet in a terminal state. Used only for the Agent pre-check below (Agent's FK is
    // SetNull, so a Cancelled/Rejected inquiry referencing it would NOT block the DB delete — the
    // "live" business rule exists purely to stop an admin from silently orphaning an active
    // assignment, not to predict a DB error).
    private static readonly string[] LiveStatuses =
        [InquiryStatuses.Submitted, InquiryStatuses.Contacted, InquiryStatuses.Confirmed];

    public async Task<IEnumerable<Inquiry>> GetByUserIdAsync(Guid userId)
        => await _dbSet.AsNoTracking()
            .Include(i => i.Service).ThenInclude(s => s.ServiceCategory).ThenInclude(c => c.ServiceSection)
            .Include(i => i.ServicePackage)
            .Include(i => i.AssignedAgent)
            .Where(i => i.UserId == userId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();

    public async Task<(IReadOnlyList<Inquiry> Items, bool HasMore)> GetAdminFilteredPagedAsync(
        string? status, Guid? serviceSectionId, int page, int pageSize)
    {
        var query = _dbSet.AsNoTracking()
            .Include(i => i.Service).ThenInclude(s => s.ServiceCategory).ThenInclude(c => c.ServiceSection)
            .Include(i => i.ServicePackage)
            .Include(i => i.AssignedAgent)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(i => i.Status == status);
        if (serviceSectionId != null)
            query = query.Where(i => i.Service.ServiceCategory.ServiceSectionId == serviceSectionId);

        var take = pageSize + 1;
        var items = await query
            .OrderByDescending(i => i.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(take)
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }

    public async Task<(IReadOnlyList<Inquiry> Items, bool HasMore)> GetByAssignedAgentIdAsync(
        Guid agentId, int page, int pageSize)
    {
        var take = pageSize + 1;
        var items = await _dbSet.AsNoTracking()
            .Include(i => i.Service).ThenInclude(s => s.ServiceCategory).ThenInclude(c => c.ServiceSection)
            .Include(i => i.ServicePackage)
            .Include(i => i.AssignedAgent)
            .Where(i => i.AssignedAgentId == agentId)
            .OrderByDescending(i => i.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(take)
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }

    public async Task<int> CountByAssignedAgentIdAndStatusAsync(Guid agentId, string status)
        => await _dbSet.AsNoTracking().CountAsync(i => i.AssignedAgentId == agentId && i.Status == status);

    public async Task<Inquiry?> GetByIdWithDetailsAsync(Guid id)
        => await _dbSet.AsNoTracking()
            .Include(i => i.Service).ThenInclude(s => s.ServiceCategory).ThenInclude(c => c.ServiceSection)
            .Include(i => i.ServicePackage)
            .Include(i => i.AssignedAgent).ThenInclude(a => a!.AgentServiceCategories).ThenInclude(ac => ac.ServiceCategory)
            .Include(i => i.StatusHistory.OrderByDescending(h => h.CreatedAt)).ThenInclude(h => h.ChangedByAdmin)
            .Include(i => i.StatusHistory).ThenInclude(h => h.ChangedByAgent)
            .FirstOrDefaultAsync(i => i.Id == id);

    // ServicePackageId's FK is Restrict (the migration never allows an orphaned Inquiry row) — so
    // ANY referencing inquiry, terminal or not, would make the raw DB delete throw. Check all
    // statuses here so the 409 pre-check matches what the DB will actually do.
    public async Task<bool> ExistsByServicePackageIdAsync(Guid servicePackageId)
        => await _dbSet.AnyAsync(i => i.ServicePackageId == servicePackageId);

    // AssignedAgentId's FK is SetNull — the DB delete would succeed silently even with referencing
    // rows, so this is a pure business-level guard restricted to "live" inquiries only.
    public async Task<bool> ExistsByAssignedAgentIdAsync(Guid agentId)
        => await _dbSet.AnyAsync(i => i.AssignedAgentId == agentId && LiveStatuses.Contains(i.Status));
}

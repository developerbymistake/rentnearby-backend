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
    // SetNull, so a Closed inquiry referencing it would NOT block the DB delete — the "live"
    // business rule exists purely to stop an admin from silently orphaning an active assignment,
    // not to predict a DB error).
    private static readonly string[] LiveStatuses =
        [InquiryStatuses.Submitted, InquiryStatuses.Contacted];

    public async Task<IEnumerable<Inquiry>> GetByUserIdAsync(Guid userId)
        => await _dbSet.AsNoTracking()
            .Include(i => i.Service).ThenInclude(s => s.ServiceCategory)
            .Include(i => i.ServicePackage)
            .Include(i => i.InquiryAgents).ThenInclude(ia => ia.Agent)
            .Include(i => i.Escalations)
            .Where(i => i.UserId == userId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();

    // Unseen formula (deliberately UpdatedAt-relative, not CreatedAt-relative — the latter breaks on
    // brand-new rows where CreatedAt == UpdatedAt): not-Closed AND (never seen OR changed since seen).
    public async Task<int> GetUnseenCountForUserAsync(Guid userId)
        => await _dbSet.AsNoTracking()
            .CountAsync(i => i.UserId == userId
                && i.Status != InquiryStatuses.Closed
                && (i.UserSeenAt == null || i.UpdatedAt > i.UserSeenAt));

    public async Task<(IReadOnlyList<Inquiry> Items, bool HasMore)> GetAdminFilteredPagedAsync(
        string? status, Guid? serviceCategoryId, bool? escalatedOnly, int page, int pageSize)
    {
        var query = _dbSet.AsNoTracking()
            .Include(i => i.Service).ThenInclude(s => s.ServiceCategory)
            .Include(i => i.ServicePackage)
            .Include(i => i.InquiryAgents).ThenInclude(ia => ia.Agent)
            .Include(i => i.Escalations)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(i => i.Status == status);
        if (serviceCategoryId != null)
            query = query.Where(i => i.Service.ServiceCategoryId == serviceCategoryId);
        if (escalatedOnly == true)
            query = query.Where(i => i.Escalations.Any(esc => esc.Status == "Pending"));

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
            .Include(i => i.Service).ThenInclude(s => s.ServiceCategory)
            .Include(i => i.ServicePackage)
            .Include(i => i.InquiryAgents).ThenInclude(ia => ia.Agent)
            .Include(i => i.Escalations)
            .Where(i => i.InquiryAgents.Any(ia => ia.AgentId == agentId))
            .OrderByDescending(i => i.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(take)
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }

    public async Task<bool> IsAgentAssignedAsync(Guid inquiryId, Guid agentId)
        => await _context.Set<InquiryAgent>().AnyAsync(ia => ia.InquiryId == inquiryId && ia.AgentId == agentId);

    // Same unseen formula as GetUnseenCountForUserAsync, scoped per-agent via the InquiryAgent join
    // row's own SeenAt (not a column on Inquiry — see InquiryAgent.cs's comment on why). This also
    // fixes the old Status == "Submitted" bug: filtering on Status != Closed instead means an
    // assigned-and-auto-transitioned-to-Contacted lead is now correctly counted.
    public async Task<int> GetUnseenCountForAgentAsync(Guid agentId)
        => await _context.Set<InquiryAgent>().AsNoTracking()
            .Where(ia => ia.AgentId == agentId
                && ia.Inquiry.Status != InquiryStatuses.Closed
                && (ia.SeenAt == null || ia.Inquiry.UpdatedAt > ia.SeenAt))
            .CountAsync();

    // Bare SQL UPDATE via ExecuteUpdateAsync — bypasses EF change-tracking and Inquiry's xmin
    // optimistic-concurrency check entirely, so this can never collide with a concurrent
    // status-update write (same idiom as CreditWalletService's atomic spend/add methods). No-op
    // (not an error) if the id/ownership doesn't match — mirrors MarkNotificationRead's idiom.
    public async Task MarkSeenByUserAsync(Guid inquiryId, Guid userId)
        => await _dbSet.Where(i => i.Id == inquiryId && i.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UserSeenAt, DateTime.UtcNow));

    public async Task MarkSeenByAgentAsync(Guid inquiryId, Guid agentId)
        => await _context.Set<InquiryAgent>()
            .Where(ia => ia.InquiryId == inquiryId && ia.AgentId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(ia => ia.SeenAt, DateTime.UtcNow));

    public async Task<Inquiry?> GetByIdWithDetailsAsync(Guid id)
        => await _dbSet.AsNoTracking()
            .Include(i => i.Service).ThenInclude(s => s.ServiceCategory)
            .Include(i => i.ServicePackage)
            .Include(i => i.InquiryAgents).ThenInclude(ia => ia.Agent)
            .Include(i => i.Escalations)
            .Include(i => i.StatusHistory.OrderByDescending(h => h.CreatedAt)).ThenInclude(h => h.ChangedByAdmin)
            .Include(i => i.StatusHistory).ThenInclude(h => h.ChangedByAgent)
            .FirstOrDefaultAsync(i => i.Id == id);

    // ServicePackageId's FK is Restrict (the migration never allows an orphaned Inquiry row) — so
    // ANY referencing inquiry, terminal or not, would make the raw DB delete throw. Check all
    // statuses here so the 409 pre-check matches what the DB will actually do.
    public async Task<bool> ExistsByServicePackageIdAsync(Guid servicePackageId)
        => await _dbSet.AnyAsync(i => i.ServicePackageId == servicePackageId);

    // InquiryAgent's FK is Cascade — the DB delete would succeed silently (removing the join row)
    // even with referencing rows, so this is a pure business-level guard restricted to "live"
    // inquiries only.
    public async Task<bool> ExistsByAssignedAgentIdAsync(Guid agentId)
        => await _dbSet.AnyAsync(i => i.InquiryAgents.Any(ia => ia.AgentId == agentId) && LiveStatuses.Contains(i.Status));
}

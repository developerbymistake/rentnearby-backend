using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class EnquiryRepository(ApplicationDbContext context)
    : Repository<Enquiry>(context), IEnquiryRepository
{
    // "Live" = not yet in a terminal state. Used only for the Agent pre-check below (Agent's FK is
    // SetNull, so a Closed enquiry referencing it would NOT block the DB delete — the "live"
    // business rule exists purely to stop an admin from silently orphaning an active assignment,
    // not to predict a DB error).
    private static readonly string[] LiveStatuses =
        [EnquiryStatuses.Submitted, EnquiryStatuses.Contacted];

    public async Task<IEnumerable<Enquiry>> GetByUserIdAsync(Guid userId)
        => await _dbSet.AsNoTracking()
            .Include(i => i.Service).ThenInclude(s => s.ServiceCategory)
            .Include(i => i.ServicePackage)
            .Include(i => i.EnquiryAgents).ThenInclude(ia => ia.Agent)
            .Include(i => i.Escalations)
            .Where(i => i.UserId == userId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();

    // Unseen formula (deliberately UpdatedAt-relative, not CreatedAt-relative — the latter breaks on
    // brand-new rows where CreatedAt == UpdatedAt): not-Closed AND (never seen OR changed since seen).
    public async Task<int> GetUnseenCountForUserAsync(Guid userId)
        => await _dbSet.AsNoTracking()
            .CountAsync(i => i.UserId == userId
                && i.Status != EnquiryStatuses.Closed
                && (i.UserSeenAt == null || i.UpdatedAt > i.UserSeenAt));

    public async Task<(IReadOnlyList<Enquiry> Items, bool HasMore)> GetAdminFilteredPagedAsync(
        string? status, Guid? serviceCategoryId, bool? escalatedOnly, int page, int pageSize)
    {
        var query = _dbSet.AsNoTracking()
            .Include(i => i.Service).ThenInclude(s => s.ServiceCategory)
            .Include(i => i.ServicePackage)
            .Include(i => i.EnquiryAgents).ThenInclude(ia => ia.Agent)
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

    public async Task<(IReadOnlyList<Enquiry> Items, bool HasMore)> GetByAssignedAgentIdAsync(
        Guid agentId, int page, int pageSize)
    {
        var take = pageSize + 1;
        var items = await _dbSet.AsNoTracking()
            .Include(i => i.Service).ThenInclude(s => s.ServiceCategory)
            .Include(i => i.ServicePackage)
            .Include(i => i.EnquiryAgents).ThenInclude(ia => ia.Agent)
            .Include(i => i.Escalations)
            .Where(i => i.EnquiryAgents.Any(ia => ia.AgentId == agentId))
            .OrderByDescending(i => i.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(take)
            .ToListAsync();

        var hasMore = items.Count > pageSize;
        return (hasMore ? items.Take(pageSize).ToList().AsReadOnly() : items.AsReadOnly(), hasMore);
    }

    public async Task<bool> IsAgentAssignedAsync(Guid enquiryId, Guid agentId)
        => await _context.Set<EnquiryAgent>().AnyAsync(ia => ia.EnquiryId == enquiryId && ia.AgentId == agentId);

    // Same unseen formula as GetUnseenCountForUserAsync, scoped per-agent via the EnquiryAgent join
    // row's own SeenAt (not a column on Enquiry — see EnquiryAgent.cs's comment on why). This also
    // fixes the old Status == "Submitted" bug: filtering on Status != Closed instead means an
    // assigned-and-auto-transitioned-to-Contacted lead is now correctly counted.
    public async Task<int> GetUnseenCountForAgentAsync(Guid agentId)
        => await _context.Set<EnquiryAgent>().AsNoTracking()
            .Where(ia => ia.AgentId == agentId
                && ia.Enquiry.Status != EnquiryStatuses.Closed
                && (ia.SeenAt == null || ia.Enquiry.UpdatedAt > ia.SeenAt))
            .CountAsync();

    // Bare SQL UPDATE via ExecuteUpdateAsync — bypasses EF change-tracking and Enquiry's xmin
    // optimistic-concurrency check entirely, so this can never collide with a concurrent
    // status-update write (same idiom as CreditWalletService's atomic spend/add methods). No-op
    // (not an error) if the id/ownership doesn't match — mirrors MarkNotificationRead's idiom.
    public async Task MarkSeenByUserAsync(Guid enquiryId, Guid userId)
        => await _dbSet.Where(i => i.Id == enquiryId && i.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UserSeenAt, DateTime.UtcNow));

    public async Task MarkSeenByAgentAsync(Guid enquiryId, Guid agentId)
        => await _context.Set<EnquiryAgent>()
            .Where(ia => ia.EnquiryId == enquiryId && ia.AgentId == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(ia => ia.SeenAt, DateTime.UtcNow));

    public async Task<Enquiry?> GetByIdWithDetailsAsync(Guid id)
        => await _dbSet.AsNoTracking()
            .Include(i => i.Service).ThenInclude(s => s.ServiceCategory)
            .Include(i => i.ServicePackage)
            .Include(i => i.EnquiryAgents).ThenInclude(ia => ia.Agent)
            .Include(i => i.Escalations)
            .Include(i => i.StatusHistory.OrderByDescending(h => h.CreatedAt)).ThenInclude(h => h.ChangedByAdmin)
            .Include(i => i.StatusHistory).ThenInclude(h => h.ChangedByAgent)
            .FirstOrDefaultAsync(i => i.Id == id);

    // ServicePackageId's FK is Restrict (the migration never allows an orphaned Enquiry row) — so
    // ANY referencing enquiry, terminal or not, would make the raw DB delete throw. Check all
    // statuses here so the 409 pre-check matches what the DB will actually do.
    public async Task<bool> ExistsByServicePackageIdAsync(Guid servicePackageId)
        => await _dbSet.AnyAsync(i => i.ServicePackageId == servicePackageId);

    // EnquiryAgent's FK is Cascade — the DB delete would succeed silently (removing the join row)
    // even with referencing rows, so this is a pure business-level guard restricted to "live"
    // enquiries only.
    public async Task<bool> ExistsByAssignedAgentIdAsync(Guid agentId)
        => await _dbSet.AnyAsync(i => i.EnquiryAgents.Any(ia => ia.AgentId == agentId) && LiveStatuses.Contains(i.Status));

    // Raw month+status counts for one agent/year — AgentHandlers.BuildLeadStatsDto zero-fills and
    // cross-tabs these into the 12-month DTO both the agent's own Dashboard and the admin's Agent
    // Stats page consume. Joins through EnquiryAgents (no direct AgentId FK on Enquiry), same as
    // ExistsByAssignedAgentIdAsync above.
    public async Task<List<MonthlyStatusCountRow>> GetMonthlyStatusCountsForAgentAsync(Guid agentId, int year)
    {
        // Range comparison, not `.Year == year` — the latter compiles to a non-sargable date-part
        // expression that can't use an index on CreatedAt regardless of what's defined; a range
        // check against the Enquiry(CreatedAt, Status) index (see OnModelCreating) lets Postgres
        // index-scan instead of sequential-scan this table.
        var startOfYear = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var startOfNextYear = startOfYear.AddYears(1);
        return await _dbSet.AsNoTracking()
            .Where(i => i.EnquiryAgents.Any(ia => ia.AgentId == agentId) && i.CreatedAt >= startOfYear && i.CreatedAt < startOfNextYear)
            .GroupBy(i => new { i.CreatedAt.Month, i.Status })
            .Select(g => new MonthlyStatusCountRow { Month = g.Key.Month, Status = g.Key.Status, Count = g.Count() })
            .ToListAsync();
    }
}

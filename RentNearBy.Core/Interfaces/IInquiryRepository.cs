using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IInquiryRepository : IRepository<Inquiry>
{
    // Consumer "My Inquiries" — shared across both verticals in one list.
    Task<IEnumerable<Inquiry>> GetByUserIdAsync(Guid userId);

    // Server-anchored "My Inquiries" badge count (Submitted/Contacted only) — a lean COUNT(*)
    // counterpart to GetByUserIdAsync, mirroring Conversations.GetTotalUnreadForUserAsync's shape.
    Task<int> GetActiveCountForUserAsync(Guid userId);

    // Admin paginated list with optional status/category/escalated-only filter chips.
    Task<(IReadOnlyList<Inquiry> Items, bool HasMore)> GetAdminFilteredPagedAsync(
        string? status, Guid? serviceCategoryId, bool? escalatedOnly, int page, int pageSize);

    // Agent's own "My Leads" — paginated, scoped to a single agentId derived server-side from the
    // caller's own JWT, never a client-supplied id.
    Task<(IReadOnlyList<Inquiry> Items, bool HasMore)> GetByAssignedAgentIdAsync(
        Guid agentId, int page, int pageSize);

    // Lean membership check against the InquiryAgent join table directly — for ownership gates that
    // run against a plain (tracked, navigation-not-loaded) GetByIdAsync fetch, where
    // inquiry.InquiryAgents can't be relied on without a separate eager-load just for one boolean.
    Task<bool> IsAgentAssignedAsync(Guid inquiryId, Guid agentId);

    // "New leads" badge count — deliberately narrower than GetByAssignedAgentIdAsync's full list
    // (Submitted only, not every live status) — see the plan's Design decisions for why.
    Task<int> CountByAssignedAgentIdAndStatusAsync(Guid agentId, string status);

    // For InquiryDetailDto assembly: Service -> ServiceCategory, ServicePackage,
    // AssignedAgent -> AgentServiceCategories, StatusHistory -> ChangedByAdmin, all in one query.
    Task<Inquiry?> GetByIdWithDetailsAsync(Guid id);

    // Pre-checks for the hard-delete-blocked-if-referenced rule on ServicePackage/Agent.
    Task<bool> ExistsByServicePackageIdAsync(Guid servicePackageId);
    Task<bool> ExistsByAssignedAgentIdAsync(Guid agentId);
}

using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IInquiryRepository : IRepository<Inquiry>
{
    // Consumer "My Inquiries" — shared across both verticals in one list.
    Task<IEnumerable<Inquiry>> GetByUserIdAsync(Guid userId);

    // Server-anchored "My Inquiries" badge count — not-Closed AND updated since the consumer last saw
    // it (UserSeenAt == null || UpdatedAt > UserSeenAt). A lean COUNT(*) counterpart to
    // GetByUserIdAsync, mirroring Conversations.GetTotalUnreadForUserAsync's shape.
    Task<int> GetUnseenCountForUserAsync(Guid userId);

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

    // "My Leads" badge count — not-Closed AND updated since this agent last saw it
    // (SeenAt == null || Inquiry.UpdatedAt > SeenAt). Replaces the old Status == "Submitted" filter,
    // which was structurally broken: both assignment code paths flip an inquiry to Contacted in the
    // very same write that assigns an agent, so an assigned lead could never actually be Submitted.
    Task<int> GetUnseenCountForAgentAsync(Guid agentId);

    // Per-viewer seen-timestamp writers, mirroring MarkNotificationRead's no-existence-leak idiom —
    // a no-op (not an error) if the id/ownership doesn't match. Bare SQL UPDATE via ExecuteUpdateAsync,
    // bypassing EF change-tracking and xmin optimistic concurrency entirely.
    Task MarkSeenByUserAsync(Guid inquiryId, Guid userId);
    Task MarkSeenByAgentAsync(Guid inquiryId, Guid agentId);

    // For InquiryDetailDto assembly: Service -> ServiceCategory, ServicePackage,
    // AssignedAgent -> Agent, StatusHistory -> ChangedByAdmin, all in one query.
    Task<Inquiry?> GetByIdWithDetailsAsync(Guid id);

    // Pre-checks for the hard-delete-blocked-if-referenced rule on ServicePackage/Agent.
    Task<bool> ExistsByServicePackageIdAsync(Guid servicePackageId);
    Task<bool> ExistsByAssignedAgentIdAsync(Guid agentId);
}

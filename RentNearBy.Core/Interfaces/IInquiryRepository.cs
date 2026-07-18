using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IInquiryRepository : IRepository<Inquiry>
{
    // Consumer "My Inquiries" — shared across both verticals in one list.
    Task<IEnumerable<Inquiry>> GetByUserIdAsync(Guid userId);

    // Admin paginated list with optional status/section filter chips.
    Task<(IReadOnlyList<Inquiry> Items, bool HasMore)> GetAdminFilteredPagedAsync(
        string? status, Guid? serviceSectionId, int page, int pageSize);

    // For InquiryDetailDto assembly: Service -> ServiceCategory -> ServiceSection, ServicePackage,
    // AssignedAgent -> AgentServiceCategories, StatusHistory -> ChangedByAdmin, all in one query.
    Task<Inquiry?> GetByIdWithDetailsAsync(Guid id);

    // Pre-checks for the hard-delete-blocked-if-referenced rule on ServicePackage/Agent.
    Task<bool> ExistsByServicePackageIdAsync(Guid servicePackageId);
    Task<bool> ExistsByAssignedAgentIdAsync(Guid agentId);
}

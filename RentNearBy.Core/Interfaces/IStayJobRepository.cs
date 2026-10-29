using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IStayJobRepository : IRepository<StayJob>
{
    Task<int> CountActiveByUserAsync(Guid userId);
    // Tracked, for the owner's own writes.
    Task<StayJob?> GetOwnedTrackedAsync(Guid id, Guid userId);
    // Active first, then newest.
    Task<List<StayJobOwnerDto>> GetOwnerListAsync(Guid userId);
    // Public: Active job on an Approved, not deleted stay whose owner is active (a paused stay keeps its jobs).
    // Filtered by district; jobs of stays in cityId come first.
    Task<(IReadOnlyList<StayJobSummaryDto> Items, bool HasMore)> BrowseAsync(
        Guid districtId, Guid? cityId, Guid? departmentId, int? minExperience, int? maxExperience,
        int? minSalary, int? maxSalary, int page, int pageSize);
    Task<StayJobDetailDto?> GetPublicDetailAsync(Guid id);
    // Any status, owner only.
    Task<StayJobDetailDto?> GetOwnerDetailAsync(Guid id, Guid userId);
    // Includes jobs whose stay is soft deleted.
    Task<(IReadOnlyList<AdminStayJobDto> Items, bool HasMore)> GetAdminPagedAsync(string? status, Guid? districtId, int page, int pageSize);
    Task<int> CloseActiveForStayAsync(Guid stayListingId);
}

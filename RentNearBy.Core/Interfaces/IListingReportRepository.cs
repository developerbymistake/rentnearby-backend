using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IListingReportRepository
{
    Task AddAsync(ListingReport report);
    Task<bool> HasPendingReportForListingAsync(Guid listingId, string listingType);
    Task<bool> HasPendingReportFromReporterAsync(Guid listingId, string listingType, Guid reporterUserId);
    Task<ListingReport?> GetByIdAsync(Guid id);
    Task<PagedResult<ListingReport>> GetPagedAsync(int page, int pageSize, string? status);
    Task<int> AutoResolvePendingForListingAsync(Guid listingId, string listingType);
    Task ResolveAsync(Guid reportId, Guid? adminId, string resolutionAction);
}

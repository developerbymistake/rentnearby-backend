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
    Task<PagedResult<ListingReport>> GetPagedForListingAsync(Guid listingId, string listingType, int page, int pageSize);
    Task<PagedResult<ListingReport>> GetPagedForReporterAsync(Guid reporterUserId, int page, int pageSize);
    Task<Dictionary<Guid, int>> GetPendingCountsForListingsAsync(IEnumerable<Guid> listingIds, string listingType);
}

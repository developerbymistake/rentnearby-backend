using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class TripOfferRepository(ApplicationDbContext context) : Repository<TripOffer>(context), ITripOfferRepository
{
    public async Task<IReadOnlyList<TripOffer>> GetByEnquiryAsync(Guid enquiryId)
        => await _dbSet
            .Where(o => o.TripEnquiryId == enquiryId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();

    public async Task<Dictionary<Guid, List<TripOffer>>> GetByEnquiriesAsync(IReadOnlyCollection<Guid> enquiryIds)
    {
        if (enquiryIds.Count == 0) return new Dictionary<Guid, List<TripOffer>>();
        var ids = enquiryIds.Distinct().ToList();
        var rows = await _dbSet.AsNoTracking()
            .Where(o => ids.Contains(o.TripEnquiryId))
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();
        return rows.GroupBy(o => o.TripEnquiryId).ToDictionary(g => g.Key, g => g.ToList());
    }

    public async Task<TripOffer?> GetPendingAsync(Guid enquiryId)
        => await _dbSet
            .Where(o => o.TripEnquiryId == enquiryId && o.Status == TripOfferStatuses.Pending)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync();

    public async Task<IReadOnlyList<TripOffer>> GetExpiredPendingAsync(DateTime nowUtc, int take)
        => await _dbSet.AsNoTracking()
            .Where(o => o.Status == TripOfferStatuses.Pending && o.ValidUntil != null && o.ValidUntil <= nowUtc)
            .OrderBy(o => o.ValidUntil)
            .Take(take)
            .ToListAsync();
}

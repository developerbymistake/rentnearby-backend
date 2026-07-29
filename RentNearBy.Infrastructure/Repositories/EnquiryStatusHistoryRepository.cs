using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class EnquiryStatusHistoryRepository(ApplicationDbContext context)
    : Repository<EnquiryStatusHistory>(context), IEnquiryStatusHistoryRepository
{
    public async Task<IEnumerable<EnquiryStatusHistory>> GetByEnquiryIdAsync(Guid enquiryId)
        => await _dbSet.AsNoTracking()
            .Include(h => h.ChangedByAdmin)
            .Where(h => h.EnquiryId == enquiryId)
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync();
}

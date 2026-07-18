using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class InquiryStatusHistoryRepository(ApplicationDbContext context)
    : Repository<InquiryStatusHistory>(context), IInquiryStatusHistoryRepository
{
    public async Task<IEnumerable<InquiryStatusHistory>> GetByInquiryIdAsync(Guid inquiryId)
        => await _dbSet.AsNoTracking()
            .Include(h => h.ChangedByAdmin)
            .Where(h => h.InquiryId == inquiryId)
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync();
}

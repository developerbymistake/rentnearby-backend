using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class ServiceRepository(ApplicationDbContext context)
    : Repository<Service>(context), IServiceRepository
{
    public async Task<IEnumerable<Service>> GetByServiceCategoryIdAsync(Guid? serviceCategoryId)
        => await _dbSet.AsNoTracking()
            .Where(s => serviceCategoryId == null || s.ServiceCategoryId == serviceCategoryId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToListAsync();

    public async Task<Service?> GetByIdWithDetailsAsync(Guid id)
        => await _dbSet.AsNoTracking()
            .Include(s => s.ServiceCategory).ThenInclude(c => c.ServiceSection)
            .Include(s => s.Packages.OrderBy(p => p.SortOrder))
            .FirstOrDefaultAsync(s => s.Id == id);

    public async Task<IEnumerable<Service>> GetPreviewByServiceSectionIdAsync(Guid serviceSectionId, int limit)
        => await _dbSet.AsNoTracking()
            .Where(s => s.IsActive && s.ServiceCategory.ServiceSectionId == serviceSectionId)
            .OrderByDescending(s => s.IsFeatured).ThenBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Take(limit)
            .ToListAsync();
}

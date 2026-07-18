using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class ListingLimitSettingRepository(ApplicationDbContext context)
    : Repository<ListingLimitSetting>(context), IListingLimitSettingRepository
{
    public async Task<ListingLimitSetting?> GetByKindAsync(string kind)
        => await context.ListingLimitSettings.FirstOrDefaultAsync(s => s.ListingKind == kind);

    public async Task<int> UpdateMaxListingsAsync(string kind, int maxListings)
        => await context.ListingLimitSettings
            .Where(s => s.ListingKind == kind)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.MaxListings, maxListings)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow));
}

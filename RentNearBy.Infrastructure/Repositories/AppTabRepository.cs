using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class AppTabRepository(ApplicationDbContext context)
    : Repository<AppTab>(context), IAppTabRepository
{
    public async Task<AppTab?> GetByKeyAsync(string tabKey)
        => await context.AppTabs.FirstOrDefaultAsync(t => t.TabKey == tabKey);
}

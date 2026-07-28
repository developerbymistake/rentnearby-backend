using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class AppSettingRepository(ApplicationDbContext context)
    : Repository<AppSetting>(context), IAppSettingRepository
{
    public async Task<AppSetting?> GetByTypeAsync(string type)
        => await context.AppSettings.FirstOrDefaultAsync(s => s.Type == type);
}

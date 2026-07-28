using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IAppSettingRepository : IRepository<AppSetting>
{
    Task<AppSetting?> GetByTypeAsync(string type);
}

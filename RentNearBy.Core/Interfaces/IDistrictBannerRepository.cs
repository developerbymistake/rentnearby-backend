using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IDistrictBannerRepository : IRepository<DistrictBanner>
{
    Task<DistrictBanner?> GetActiveForUserAsync(Guid districtId, Guid userId);
    Task<DistrictBanner?> GetByDistrictIdAsync(Guid districtId);
    Task<IEnumerable<DistrictBanner>> GetAllWithDistrictAsync();
}

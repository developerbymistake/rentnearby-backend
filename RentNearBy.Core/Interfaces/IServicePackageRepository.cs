using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IServicePackageRepository : IRepository<ServicePackage>
{
    // serviceId == null returns every package.
    Task<IEnumerable<ServicePackage>> GetByServiceIdAsync(Guid? serviceId);
}

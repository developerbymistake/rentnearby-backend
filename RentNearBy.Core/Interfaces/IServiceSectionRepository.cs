using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IServiceSectionRepository : IRepository<ServiceSection>
{
    Task<IEnumerable<ServiceSection>> GetAllOrderedAsync();
}

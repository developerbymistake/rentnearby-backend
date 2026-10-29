using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class StayTypeRepository : Repository<StayType>, IStayTypeRepository
{
    public StayTypeRepository(ApplicationDbContext context) : base(context) { }
}

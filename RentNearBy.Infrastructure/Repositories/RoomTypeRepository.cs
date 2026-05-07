using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class RoomTypeRepository(ApplicationDbContext context) : Repository<RoomType>(context), IRoomTypeRepository
{
}

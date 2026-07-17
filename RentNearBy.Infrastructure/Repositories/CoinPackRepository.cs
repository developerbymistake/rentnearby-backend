using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class CoinPackRepository(ApplicationDbContext context) : Repository<CoinPack>(context), ICoinPackRepository
{
}

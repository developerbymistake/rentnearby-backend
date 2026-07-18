using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class CouponRedemptionRepository(ApplicationDbContext context) : Repository<CouponRedemption>(context), ICouponRedemptionRepository
{
}

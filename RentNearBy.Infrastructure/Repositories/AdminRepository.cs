using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class AdminRepository(ApplicationDbContext context) : Repository<Admin>(context), IAdminRepository
{
    public async Task<Admin?> GetByPhoneAsync(string phoneNumber)
        => await _dbSet.AsNoTracking()
            .FirstOrDefaultAsync(a => a.PhoneNumber == phoneNumber);
}

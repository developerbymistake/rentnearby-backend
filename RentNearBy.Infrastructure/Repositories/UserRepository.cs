using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class UserRepository(ApplicationDbContext context) : Repository<User>(context), IUserRepository
{
    public async Task<User?> GetByPhoneAsync(string phoneNumber)
        => await _dbSet.AsNoTracking()
            .FirstOrDefaultAsync(u => u.PhoneNumber == phoneNumber);

    public async Task<bool> PhoneExistsAsync(string phoneNumber)
        => await _dbSet.AnyAsync(u => u.PhoneNumber == phoneNumber);
}

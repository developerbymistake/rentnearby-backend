using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class UserBlockRepository : Repository<UserBlock>, IUserBlockRepository
{
    public UserBlockRepository(ApplicationDbContext context) : base(context) { }

    public async Task<bool> ExistsAsync(Guid blockerId, Guid blockedId)
        => await _context.UserBlocks.AnyAsync(b => b.BlockerId == blockerId && b.BlockedId == blockedId);
}

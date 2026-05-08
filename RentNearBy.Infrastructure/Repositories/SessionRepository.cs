using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class SessionRepository(ApplicationDbContext context) : Repository<Session>(context), ISessionRepository
{
    public async Task DeleteAllUserSessionsAsync(Guid userId)
    {
        await _context.Sessions
            .Where(s => s.UserId == userId)
            .ExecuteDeleteAsync();
    }

    public async Task<Session?> GetActiveSessionAsync(Guid sessionId)
    {
        return await _context.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.ExpiresAt > DateTime.UtcNow);
    }
}

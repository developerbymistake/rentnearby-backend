using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class SessionRepository(ApplicationDbContext context) : Repository<Session>(context), ISessionRepository
{
    public async Task RevokeAllUserSessionsAsync(Guid userId)
    {
        await _context.Sessions
            .Where(s => s.UserId == userId && !s.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRevoked, true));
    }

    public async Task<Session?> GetActiveSessionAsync(Guid sessionId)
    {
        return await _context.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId && !s.IsRevoked && s.ExpiresAt > DateTime.UtcNow);
    }
}

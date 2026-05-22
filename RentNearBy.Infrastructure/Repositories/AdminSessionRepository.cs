using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class AdminSessionRepository(ApplicationDbContext context) : Repository<AdminSession>(context), IAdminSessionRepository
{
    public async Task DeleteAllAdminSessionsAsync(Guid adminId)
    {
        await _context.AdminSessions
            .Where(s => s.AdminId == adminId)
            .ExecuteDeleteAsync();
    }

    public async Task<AdminSession?> GetActiveAdminSessionAsync(Guid sessionId)
    {
        return await _context.AdminSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.ExpiresAt > DateTime.UtcNow);
    }
}

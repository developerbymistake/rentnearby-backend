using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IAdminSessionRepository : IRepository<AdminSession>
{
    Task DeleteAllAdminSessionsAsync(Guid adminId);
    Task<AdminSession?> GetActiveAdminSessionAsync(Guid sessionId);
}

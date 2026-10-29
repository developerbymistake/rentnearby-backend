namespace RentNearBy.Core.Interfaces;

public interface ISessionRepository : IRepository<Entities.Session>
{
    Task DeleteAllUserSessionsAsync(Guid userId);
    Task RevokeAllUserSessionsAsync(Guid userId);
    Task<int> RevokeSessionAsync(Guid userId, Guid sessionId);
    Task<Entities.Session?> GetActiveSessionAsync(Guid sessionId);
}

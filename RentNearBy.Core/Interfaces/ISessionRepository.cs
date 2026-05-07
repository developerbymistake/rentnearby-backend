namespace RentNearBy.Core.Interfaces;

public interface ISessionRepository : IRepository<Entities.Session>
{
    Task RevokeAllUserSessionsAsync(Guid userId);
    Task<Entities.Session?> GetActiveSessionAsync(Guid sessionId);
}

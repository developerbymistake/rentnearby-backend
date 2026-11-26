namespace RentNearBy.Core.Interfaces;

public interface ISessionRepository : IRepository<Entities.Session>
{
    Task DeleteAllUserSessionsAsync(Guid userId);
    Task RevokeAllUserSessionsAsync(Guid userId, string? reason = null);
    // Only read after a failed session check (FB-42): the stored reason of a server-ended session, else null.
    Task<string?> GetRevokeReasonAsync(Guid sessionId);
    Task<int> RevokeSessionAsync(Guid userId, Guid sessionId);
    Task<Entities.Session?> GetActiveSessionAsync(Guid sessionId);
    // Same predicate as GetActiveSessionAsync; the live role of the session's user, null when the session is not active.
    Task<Guid?> GetActiveSessionRoleAsync(Guid sessionId);
}

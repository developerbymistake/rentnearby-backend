using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IRoleChangeRequestRepository : IRepository<RoleChangeRequest>
{
    Task<RoleChangeRequest?> GetLatestForUserAsync(Guid userId);
    Task<int> ResolveIfPendingAsync(Guid requestId, string status, Guid? adminId, string? rejectionReason);
    Task<int> ResolvePendingForUserAsync(Guid userId, Guid targetRoleId, Guid? adminId, string rejectionReason);
}

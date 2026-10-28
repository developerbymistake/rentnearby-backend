using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class RoleChangeRequestRepository(ApplicationDbContext context) : Repository<RoleChangeRequest>(context), IRoleChangeRequestRepository
{
    public async Task<RoleChangeRequest?> GetLatestForUserAsync(Guid userId)
        => await _dbSet.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync();

    public async Task<int> ResolveIfPendingAsync(Guid requestId, string status, Guid? adminId, string? rejectionReason)
        => await _dbSet
            .Where(r => r.Id == requestId && r.Status == RoleChangeRequestStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.ReviewedAt, DateTime.UtcNow)
                .SetProperty(r => r.ReviewedByAdminId, adminId)
                .SetProperty(r => r.RejectionReason, rejectionReason));

    public async Task<int> ResolvePendingForUserAsync(Guid userId, Guid targetRoleId, Guid? adminId, string rejectionReason)
        => await _dbSet
            .Where(r => r.UserId == userId && r.Status == RoleChangeRequestStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, r => r.ToRoleId == targetRoleId
                    ? RoleChangeRequestStatuses.Approved
                    : RoleChangeRequestStatuses.Rejected)
                .SetProperty(r => r.RejectionReason, r => r.ToRoleId == targetRoleId ? null : rejectionReason)
                .SetProperty(r => r.ReviewedAt, DateTime.UtcNow)
                .SetProperty(r => r.ReviewedByAdminId, adminId));
}

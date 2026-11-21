using Microsoft.EntityFrameworkCore;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner.Be4;

// Contract 6.1: wherever the server revokes a user's sessions (logout, suspend, admin role change) his device tokens go in the same
// transaction (one session per user), so no push, reminder or festival notice reaches a logged-out phone. One statement.
public static class DeviceTokenCleanup
{
    public static Task<int> DeleteAllAsync(ApplicationDbContext db, Guid userId, CancellationToken ct = default)
        => db.DeviceTokens.Where(d => d.UserId == userId).ExecuteDeleteAsync(ct);
}

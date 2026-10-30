using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

public class AccountDeletionService(
    ApplicationDbContext context,
    IListingPurgeService listingPurgeService,
    IPlaySubscriptionRepository playSubscriptions,
    IOptions<PlayBillingOptions> playOptions)
    : IAccountDeletionService
{
    public async Task DeleteAccountAsync(Guid userId)
    {
        using var tx = await context.Database.BeginTransactionAsync();

        // Owner lock first, same order as every grant/refund/role path (Users, then plans/purchases/listings).
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE");

        var user = await context.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.PhoneNumber, u.Name, u.CreatedAt })
            .FirstOrDefaultAsync();
        if (user != null)
        {
            context.DeletedAccountRecords.Add(new DeletedAccountRecord
            {
                Id = Guid.NewGuid(),
                OriginalUserId = userId,
                PhoneNumber = user.PhoneNumber,
                Name = user.Name,
                AccountCreatedAt = user.CreatedAt,
                DeletedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        await context.DeviceTokens
            .Where(d => d.UserId == userId)
            .ExecuteDeleteAsync();

        await context.NotificationLogs
            .Where(n => n.UserId == userId)
            .ExecuteDeleteAsync();

        await context.Sessions
            .Where(s => s.UserId == userId)
            .ExecuteDeleteAsync();

        // Plans end with the account; UserPlans then cascade-delete with the user. Purchase rows are
        // financial records: kept, anonymised (UserId null). An order still open is closed so a late
        // capture lands as Paid + RoleMismatch and the reconciler refunds it. CouponRedemptions.UserId
        // is nulled by its SetNull FK.
        var now = DateTime.UtcNow;
        await context.UserPlans
            .Where(u => u.UserId == userId && u.Status == UserPlanStatuses.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Status, UserPlanStatuses.Cancelled)
                .SetProperty(u => u.EndsAt, u => u.StartsAt < now ? now : u.StartsAt.AddSeconds(1))
                .SetProperty(u => u.UpdatedAt, now));

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH closed AS (
                UPDATE "PlanPurchases" SET "Status" = 'Expired', "FailureReason" = 'Account deleted'
                WHERE "UserId" = {userId} AND "Status" IN ('Created', 'Pending')
                RETURNING "Id"
            ),
            released AS (
                UPDATE "CouponRedemptions" r SET "Status" = 'Released', "UpdatedAt" = now()
                FROM closed WHERE r."PurchaseId" = closed."Id" AND r."Status" = 'Reserved'
                RETURNING r."CouponId"
            )
            UPDATE "Coupons" c SET
                "RedemptionCount" = GREATEST(c."RedemptionCount" - 1, 0),
                "Status" = CASE WHEN c."Status" = 'Exhausted' THEN 'Active' ELSE c."Status" END,
                "UpdatedAt" = now()
            FROM released WHERE c."Id" = released."CouponId"
            """);

        await context.PlanPurchases
            .Where(p => p.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.UserId, (Guid?)null)
                .SetProperty(p => p.AnonymisedAt, now));

        // Google subscriptions outlive the account: kept anonymised, Detached, and stopped on Google's side by
        // the reconciler (PlayBilling:RoleChangeAction) so nobody keeps paying for a deleted account.
        await playSubscriptions.AnonymiseForUserAsync(userId, now, playOptions.Value.RoleChangeAction);

        await listingPurgeService.PurgeUserListingsAsync(userId);

        await context.Users
            .Where(u => u.Id == userId)
            .ExecuteDeleteAsync();

        await tx.CommitAsync();
    }
}

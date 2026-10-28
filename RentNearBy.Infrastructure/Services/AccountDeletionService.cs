using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

public class AccountDeletionService(ApplicationDbContext context, IListingPurgeService listingPurgeService)
    : IAccountDeletionService
{
    public async Task DeleteAccountAsync(Guid userId)
    {
        using var tx = await context.Database.BeginTransactionAsync();

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

        // Wallet/CreditTransaction/CreditPackPurchase/CouponRedemption all cascade-delete via their
        // User FK — no explicit cleanup needed here, same as Sessions/DeviceTokens above.

        await listingPurgeService.PurgeUserListingsAsync(userId);

        await context.Users
            .Where(u => u.Id == userId)
            .ExecuteDeleteAsync();

        await tx.CommitAsync();
    }
}

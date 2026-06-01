using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

public class AccountDeletionService(ApplicationDbContext context, IPhotoService photoService)
    : IAccountDeletionService
{
    public async Task DeleteAccountAsync(Guid userId)
    {
        // Delete all Cloudinary photos for this user (covers listings + plots)
        await photoService.DeleteAllUserPhotosAsync(userId);

        using var tx = await context.Database.BeginTransactionAsync();

        await context.DeviceTokens
            .Where(d => d.UserId == userId)
            .ExecuteDeleteAsync();

        await context.NotificationLogs
            .Where(n => n.UserId == userId)
            .ExecuteDeleteAsync();

        await context.Sessions
            .Where(s => s.UserId == userId)
            .ExecuteDeleteAsync();

        await context.RoomMemberships
            .Where(m => m.UserId == userId)
            .ExecuteDeleteAsync();

        await context.PlotMemberships
            .Where(m => m.UserId == userId)
            .ExecuteDeleteAsync();

        await context.RoomPhotos
            .Where(p => p.RoomListing.UserId == userId)
            .ExecuteDeleteAsync();

        await context.RoomListings
            .Where(l => l.UserId == userId)
            .ExecuteDeleteAsync();

        await context.Set<PlotPhoto>()
            .Where(p => p.PlotListing.UserId == userId)
            .ExecuteDeleteAsync();

        await context.PlotListings
            .Where(p => p.UserId == userId)
            .ExecuteDeleteAsync();

        await context.Users
            .Where(u => u.Id == userId)
            .ExecuteDeleteAsync();

        // PaymentTransactions.UserId → SET NULL via FK constraint (no explicit delete)

        await tx.CommitAsync();
    }
}

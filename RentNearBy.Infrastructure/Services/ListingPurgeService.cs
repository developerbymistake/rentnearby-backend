using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Opens no transaction: the caller owns it. Role-change photo deletion runs after the caller's commit.
public class ListingPurgeService(
    ApplicationDbContext context, IPhotoService photoService, ILogger<ListingPurgeService> logger)
    : IListingPurgeService
{
    public async Task PurgeUserListingsAsync(Guid userId)
    {
        await photoService.DeleteAllUserPhotosAsync(userId);

        await context.RoomPhotos
            .Where(p => context.RoomListings.Any(l => l.Id == p.RoomListingId && l.UserId == userId))
            .ExecuteDeleteAsync();

        await context.RoomListings
            .Where(l => l.UserId == userId)
            .ExecuteDeleteAsync();

        await context.PlotPhotos
            .Where(p => context.PlotListings.Any(l => l.Id == p.PlotId && l.UserId == userId))
            .ExecuteDeleteAsync();

        await context.PlotListings
            .Where(p => p.UserId == userId)
            .ExecuteDeleteAsync();
    }

    public Task<IReadOnlyList<Guid>> DeleteUserListingRowsAsync(Guid userId, string listingKind) => listingKind switch
    {
        ListingKinds.Room => DeleteRoomRowsAsync(userId),
        ListingKinds.Plot => DeletePlotRowsAsync(userId),
        _ => throw new ArgumentOutOfRangeException(nameof(listingKind), listingKind, "Unknown listing kind"),
    };

    // Runs after the caller's commit: a Cloudinary failure only orphans files, it must never surface.
    public async Task DeleteListingPhotosBestEffortAsync(Guid userId, IReadOnlyList<Guid> listingIds)
    {
        var failedIds = new List<Guid>();
        Exception? lastError = null;

        // Plot photos share the listing-id prefix scheme used by DeleteRoomPhotosAsync.
        foreach (var listingId in listingIds)
        {
            try
            {
                await photoService.DeleteRoomPhotosAsync(userId, listingId);
            }
            catch (Exception ex)
            {
                failedIds.Add(listingId);
                lastError = ex;
            }
        }

        if (failedIds.Count > 0)
            logger.LogError(lastError,
                "Cloudinary photo cleanup failed for user {UserId}, listings {ListingIds}",
                userId, string.Join(",", failedIds));
    }

    private async Task<IReadOnlyList<Guid>> DeleteRoomRowsAsync(Guid userId)
    {
        var listingIds = await context.RoomListings
            .Where(l => l.UserId == userId)
            .Select(l => l.Id)
            .ToListAsync();

        await context.RoomPhotos
            .Where(p => context.RoomListings.Any(l => l.Id == p.RoomListingId && l.UserId == userId))
            .ExecuteDeleteAsync();

        await context.RoomListings
            .Where(l => l.UserId == userId)
            .ExecuteDeleteAsync();

        return listingIds;
    }

    private async Task<IReadOnlyList<Guid>> DeletePlotRowsAsync(Guid userId)
    {
        var plotIds = await context.PlotListings
            .Where(p => p.UserId == userId)
            .Select(p => p.Id)
            .ToListAsync();

        await context.PlotPhotos
            .Where(p => context.PlotListings.Any(l => l.Id == p.PlotId && l.UserId == userId))
            .ExecuteDeleteAsync();

        await context.PlotListings
            .Where(p => p.UserId == userId)
            .ExecuteDeleteAsync();

        return plotIds;
    }
}

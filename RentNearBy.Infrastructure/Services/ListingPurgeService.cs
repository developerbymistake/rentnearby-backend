using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Opens no transaction: callers run it inside their own transaction on the same scoped context.
public class ListingPurgeService(ApplicationDbContext context, IPhotoService photoService)
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

    public async Task PurgeUserListingsAsync(Guid userId, string listingKind)
    {
        switch (listingKind)
        {
            case ListingKinds.Room:
                await PurgeRoomsAsync(userId);
                break;
            case ListingKinds.Plot:
                await PurgePlotsAsync(userId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(listingKind), listingKind, "Unknown listing kind");
        }
    }

    private async Task PurgeRoomsAsync(Guid userId)
    {
        var listingIds = await context.RoomListings
            .Where(l => l.UserId == userId)
            .Select(l => l.Id)
            .ToListAsync();

        foreach (var listingId in listingIds)
            await photoService.DeleteRoomPhotosAsync(userId, listingId);

        await context.RoomPhotos
            .Where(p => context.RoomListings.Any(l => l.Id == p.RoomListingId && l.UserId == userId))
            .ExecuteDeleteAsync();

        await context.RoomListings
            .Where(l => l.UserId == userId)
            .ExecuteDeleteAsync();
    }

    private async Task PurgePlotsAsync(Guid userId)
    {
        var plotIds = await context.PlotListings
            .Where(p => p.UserId == userId)
            .Select(p => p.Id)
            .ToListAsync();

        // Plot photos share the listing-id prefix scheme used by DeleteRoomPhotosAsync.
        foreach (var plotId in plotIds)
            await photoService.DeleteRoomPhotosAsync(userId, plotId);

        await context.PlotPhotos
            .Where(p => context.PlotListings.Any(l => l.Id == p.PlotId && l.UserId == userId))
            .ExecuteDeleteAsync();

        await context.PlotListings
            .Where(p => p.UserId == userId)
            .ExecuteDeleteAsync();
    }
}

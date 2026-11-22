using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Opens no transaction: the caller owns it. Role-change photo deletion runs after the caller's commit.
public class ListingPurgeService(
    ApplicationDbContext context, IPhotoService photoService, IOwnerListingPurge ownerPurge, ILogger<ListingPurgeService> logger)
    : IListingPurgeService
{
    // Photo files are deleted by the caller after its commit (DeleteAllUserPhotosBestEffortAsync), never inside the transaction.
    public async Task<(IReadOnlyList<Guid> StayIds, IReadOnlyList<Guid> TaxiIds)> PurgeUserListingsAsync(Guid userId)
    {
        var stayIds = await DeleteStayRowsAsync(userId);
        var taxiIds = await DeleteTaxiRowsAsync(userId);
        return (stayIds, taxiIds);
    }

    // Role purge (correction and admin role change): the kind's rows, then the owner notices and festival marks of the role (AU-25).
    public async Task<IReadOnlyList<Guid>> DeleteUserListingRowsAsync(Guid userId, string listingKind)
    {
        var ids = listingKind switch
        {
            ListingKinds.Stay => await DeleteStayRowsAsync(userId),
            ListingKinds.Taxi => await DeleteTaxiRowsAsync(userId),
            _ => throw new ArgumentOutOfRangeException(nameof(listingKind), listingKind, "Unknown listing kind"),
        };
        await context.Database.ExecuteSqlRawAsync(OwnerRoleNoticesSql,
            new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = userId },
            new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = NoticeKinds.Owner.ToArray() });
        await context.Database.ExecuteSqlRawAsync(FestivalMarksSql, new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = userId });
        return ids;
    }

    internal const string OwnerRoleNoticesSql = """
        DELETE FROM "UserNotices" WHERE "UserId" = @user AND "Kind" = ANY(@kinds)
        """;

    internal const string FestivalMarksSql = """
        DELETE FROM "FestivalReminderSent" WHERE "UserId" = @user
        """;

    // After the account deletion commit: a Cloudinary failure only orphans files and is logged, it never surfaces.
    public async Task DeleteAllUserPhotosBestEffortAsync(Guid userId)
    {
        try
        {
            await photoService.DeleteAllUserPhotosAsync(userId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cloudinary photo cleanup failed for deleted user {UserId}", userId);
        }
    }

    // Runs after the caller's commit: a Cloudinary failure only orphans files, it must never surface.
    public async Task DeleteListingPhotosBestEffortAsync(Guid userId, IReadOnlyList<Guid> listingIds)
    {
        var failedIds = new List<Guid>();
        Exception? lastError = null;

        // Stay and taxi photos share the listing_{id} prefix scheme used by DeleteRoomPhotosAsync.
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

    // Stay photos live under the same listing_{id} Cloudinary prefix, so photo cleanup needs no stay branch.
    private async Task<IReadOnlyList<Guid>> DeleteStayRowsAsync(Guid userId)
    {
        var stayIds = await context.StayListings
            .Where(s => s.UserId == userId)
            .Select(s => s.Id)
            .ToListAsync();

        // Owner tables first (calendar entries with money notes, blocked ranges, special prices, weekly stats), contract 6.2 step 6.
        await ownerPurge.PurgeAsync(ListingKinds.Stay, stayIds);

        await context.StayJobs
            .Where(j => j.UserId == userId)
            .ExecuteDeleteAsync();

        // Hidden rows too (contract 3.3): the purge is on the allow-list that lifts the HiddenByPlan filter.
        await context.StayPhotos
            .IgnoreQueryFilters()
            .Where(p => context.StayListings.Any(s => s.Id == p.StayListingId && s.UserId == userId))
            .ExecuteDeleteAsync();

        await context.StayRooms
            .IgnoreQueryFilters()
            .Where(r => context.StayListings.Any(s => s.Id == r.StayListingId && s.UserId == userId))
            .ExecuteDeleteAsync();

        await context.StayListings
            .Where(s => s.UserId == userId)
            .ExecuteDeleteAsync();

        return stayIds;
    }

    // The plate and display photos live under listing_{taxiId}, so the shared photo cleanup covers both.
    private async Task<IReadOnlyList<Guid>> DeleteTaxiRowsAsync(Guid userId)
    {
        var taxiIds = await context.TaxiListings
            .Where(t => t.UserId == userId)
            .Select(t => t.Id)
            .ToListAsync();

        await ownerPurge.PurgeAsync(ListingKinds.Taxi, taxiIds);

        await context.TaxiRoutes
            .IgnoreQueryFilters()
            .Where(r => context.TaxiListings.Any(t => t.Id == r.TaxiListingId && t.UserId == userId))
            .ExecuteDeleteAsync();

        await context.TaxiListings
            .Where(t => t.UserId == userId)
            .ExecuteDeleteAsync();

        return taxiIds;
    }
}

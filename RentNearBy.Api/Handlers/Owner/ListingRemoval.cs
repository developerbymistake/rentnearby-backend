using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;

namespace RentNearBy.Api.Handlers.Owner;

public sealed record ListingRemovalResult(bool Concurrent, int CancelledBookings);

// FB-09 (doc 6.4, 8.5, 8.5a; NFR 4.2): the one soft delete of a stay or a taxi, used by the owner (DELETE /stays|taxis/me)
// and by the admin (DELETE /admin/stays|taxis/{id}). One transaction, in this order: trip closure (for a taxi it locks the
// requests first, lock order request -> driver listing) -> soft delete + pending live edit dropped -> owner tables purge
// (calendar, blocks, special prices, weekly stats) -> close the stay's jobs -> commit. After commit: closure pushes,
// reports auto-resolved, cache bust, photo files best effort (never before the commit, so a refused delete keeps them).
public static class ListingRemoval
{
    public static async Task<ListingRemovalResult> SoftDeleteStayAsync(
        StayListing stay, IUnitOfWork unitOfWork, ApplicationDbContext db, ITripEnquiryClosure tripClosure,
        IOwnerListingPurge ownerPurge, IListingPurgeService listingPurge, IServiceProvider sp)
    {
        var now = DateTime.UtcNow;
        var today = IstTime.TodayIst(now);
        IReadOnlyList<TripClosurePush> tripPushes;
        int cancelled;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            cancelled = await db.TripEnquiries.AsNoTracking().CountAsync(e => e.ListingId == stay.Id
                && e.Kind == TripEnquiryKinds.Stay && e.Status == TripEnquiryStatuses.ConfirmedByBoth && e.CheckOut >= today);
            tripPushes = await tripClosure.OnListingsRemovedAsync(ListingKinds.Stay, [stay.Id]);
            stay.IsDeleted = true;
            stay.DeletedAt = now;
            stay.UpdatedAt = now;
            stay.PendingEditJson = null;
            stay.PendingEditAt = null;
            await unitOfWork.SaveChangesAsync();
            await ownerPurge.PurgeAsync(ListingKinds.Stay, [stay.Id]);
            await unitOfWork.StayJobs.CloseActiveForStayAsync(stay.Id);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return new ListingRemovalResult(true, 0);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
        await tripClosure.AfterCommitAsync(tripPushes);
        await unitOfWork.ListingReports.AutoResolvePendingForListingAsync(stay.Id, ListingKinds.Stay);
        await StayCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), stay.DistrictId);
        await listingPurge.DeleteListingPhotosBestEffortAsync(stay.UserId, [stay.Id]);
        return new ListingRemovalResult(false, cancelled);
    }

    public static async Task<ListingRemovalResult> SoftDeleteTaxiAsync(
        TaxiListing taxi, IUnitOfWork unitOfWork, ApplicationDbContext db, ITripEnquiryClosure tripClosure,
        IOwnerListingPurge ownerPurge, IListingPurgeService listingPurge, IServiceProvider sp)
    {
        var now = DateTime.UtcNow;
        var today = IstTime.TodayIst(now);
        IReadOnlyList<TripClosurePush> tripPushes;
        int cancelled;
        string[] active = [.. BookingStates.Active];
        await unitOfWork.BeginTransactionAsync();
        try
        {
            // FB-51: active request bookings plus confirmed trip leads still to come.
            cancelled = await db.Set<TaxiBooking>().AsNoTracking().CountAsync(b => b.DriverListingId == taxi.Id && active.Contains(b.Status))
                + await CountConfirmedLeadsAsync(db, taxi.Id, today);
            tripPushes = await tripClosure.OnListingsRemovedAsync(ListingKinds.Taxi, [taxi.Id]);
            taxi.IsDeleted = true;
            taxi.DeletedAt = now;
            taxi.UpdatedAt = now;
            await unitOfWork.SaveChangesAsync();
            await ownerPurge.PurgeAsync(ListingKinds.Taxi, [taxi.Id]);
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return new ListingRemovalResult(true, 0);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
        await tripClosure.AfterCommitAsync(tripPushes);
        await unitOfWork.ListingReports.AutoResolvePendingForListingAsync(taxi.Id, ListingKinds.Taxi);
        await TaxiCache.InvalidateAsync(sp.GetService<IConnectionMultiplexer>(), taxi.DistrictId);
        await listingPurge.DeleteListingPhotosBestEffortAsync(taxi.UserId, [taxi.Id]);
        return new ListingRemovalResult(false, cancelled);
    }

    // FB-51: confirmed taxi leads of the driver whose last day is today or later (the same number the hub warns with).
    public static Task<int> CountConfirmedLeadsAsync(ApplicationDbContext db, Guid taxiId, DateOnly today)
        => RentNearBy.Infrastructure.Services.Owner.Be3.TaxiHubReader.ConfirmedLeadsAsync(db, taxiId, today);
}

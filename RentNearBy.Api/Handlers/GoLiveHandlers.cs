using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Go Live for rooms and plots. Every activation: transaction -> owner lock -> limits -> live count ->
// mutate (xmin-guarded) -> commit. First-ever Go Live goes Pending for admin approval (counts as live).
public static class GoLiveHandlers
{
    // Best-effort, after commit.
    private static async Task PublishGoLiveRequestedAsync(IRabbitMqPublisher publisher, Guid listingId, string listingType)
    {
        try
        {
            var message = new GoLiveRequestedMessage { ListingId = listingId, ListingType = listingType };
            await publisher.PublishAsync("golive.requested", JsonSerializer.Serialize(message));
        }
        catch { }
    }

    internal static IResult PlanLimitReached(int limit, int live) => ErrorResponse(409,
        limit <= 1
            ? "You can keep 1 listing live on your plan. Pause one or upgrade."
            : $"You can keep {limit} listings live on your plan. Pause one or upgrade.",
        PlanErrorTypes.PlanLimitReached, new { limit, live });

    internal static IResult ListingLimitReached(int limit, int used) => ErrorResponse(409,
        limit <= 1
            ? "Your plan allows 1 listing. Delete one or upgrade to add more."
            : $"Your plan allows {limit} listings. Delete one or upgrade to add more.",
        PlanErrorTypes.ListingLimitReached, new { limit, used });

    internal static IResult PausedByAdmin() => ForbiddenResponse(
        "This listing was paused by the admin. Contact support.", PlanErrorTypes.ListingPausedByAdmin);

    private static int DaysUntil(DateTime validUntil)
        => Math.Max(1, (int)Math.Ceiling((validUntil - DateTime.UtcNow).TotalDays));

    public static async Task<IResult> GoLiveRoom(
        Guid listingId,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        ILiveSlotService slots,
        IPlanEntitlementService entitlements,
        IListingCacheInvalidator cacheInvalidator,
        IRateLimitService rateLimiter,
        IRabbitMqPublisher publisher)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var rl = await rateLimiter.CheckAsync($"golive:room:{listingId}", maxAttempts: 1, window: TimeSpan.FromSeconds(5));
        if (!rl.IsAllowed)
            return TooManyRequestsResponse();

        GoLiveResultDto result;
        Guid districtId;
        bool submitted;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(userId);
            var listing = await unitOfWork.RoomListings.GetByIdAsync(listingId);
            IResult? fail = null;
            if (listing == null || listing.IsDeleted) fail = NotFoundResponse("RoomListing not found");
            else if (listing.UserId != userId) fail = ForbiddenResponse("You do not own this listing");
            else if (listing.IsActive && listing.ValidUntil > DateTime.UtcNow) fail = BadRequestResponse("This listing is already live.");
            else if (listing.PauseReason is PauseReasons.Admin or PauseReasons.Suspended) fail = PausedByAdmin();
            else if (listing.LiveRequestStatus == GoLiveRequestStatuses.Pending)
                fail = BadRequestResponse("This listing already has a pending Go-Live request awaiting review.");
            else if (listing.LiveRequestStatus == GoLiveRequestStatuses.Rejected)
                fail = BadRequestResponse("This listing was rejected. Delete it and create a new listing to try again.");
            if (fail != null)
            {
                await unitOfWork.RollbackTransactionAsync();
                return fail;
            }

            var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Room);
            var live = await slots.CountLiveAsync(userId, ListingKinds.Room, listing!.Id);
            if (live >= limits.ListingLimit)
            {
                await unitOfWork.RollbackTransactionAsync();
                return PlanLimitReached(limits.ListingLimit, live);
            }

            var validUntil = entitlements.ResolveValidUntil(limits);
            submitted = listing.LiveRequestStatus != GoLiveRequestStatuses.Approved;
            if (submitted)
            {
                listing.LiveRequestStatus = GoLiveRequestStatuses.Pending;
                listing.RequestedPlanDays = DaysUntil(validUntil);
            }
            else
            {
                listing.IsActive = true;
                listing.ValidUntil = validUntil;
            }
            listing.PauseReason = null;
            listing.PriorityRank = limits.PriorityRank;
            listing.IsFeatured = limits.IsFeatured;
            listing.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.RoomListings.UpdateAsync(listing);
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This listing was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
            await unitOfWork.CommitTransactionAsync();

            districtId = listing.DistrictId;
            result = new GoLiveResultDto
            {
                ValidUntil = submitted ? null : listing.ValidUntil,
                LiveRequestStatus = listing.LiveRequestStatus,
                Limits = new LiveLimitsDto { ListingLimit = limits.ListingLimit, Live = live + 1 },
            };
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        if (submitted) await PublishGoLiveRequestedAsync(publisher, listingId, ListingKinds.Room);
        else await cacheInvalidator.BustDistrictsAsync(ListingKinds.Room, [districtId]);
        return OkResponse(result);
    }

    public static async Task<IResult> GoLivePlot(
        Guid plotId,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        ILiveSlotService slots,
        IPlanEntitlementService entitlements,
        IListingCacheInvalidator cacheInvalidator,
        IRateLimitService rateLimiter,
        IRabbitMqPublisher publisher)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var rl = await rateLimiter.CheckAsync($"golive:plot:{plotId}", maxAttempts: 1, window: TimeSpan.FromSeconds(5));
        if (!rl.IsAllowed)
            return TooManyRequestsResponse();

        GoLiveResultDto result;
        Guid districtId;
        bool submitted;
        await unitOfWork.BeginTransactionAsync();
        try
        {
            await slots.LockOwnerAsync(userId);
            var plot = await unitOfWork.PlotListings.GetByIdAsync(plotId);
            IResult? fail = null;
            if (plot == null || plot.IsDeleted) fail = NotFoundResponse("PlotListing not found");
            else if (plot.UserId != userId) fail = ForbiddenResponse("You do not own this plot");
            else if (plot.IsActive && plot.ValidUntil > DateTime.UtcNow) fail = BadRequestResponse("This plot is already live.");
            else if (plot.PauseReason is PauseReasons.Admin or PauseReasons.Suspended) fail = PausedByAdmin();
            else if (plot.LiveRequestStatus == GoLiveRequestStatuses.Pending)
                fail = BadRequestResponse("This plot already has a pending Go-Live request awaiting review.");
            else if (plot.LiveRequestStatus == GoLiveRequestStatuses.Rejected)
                fail = BadRequestResponse("This plot was rejected. Delete it and create a new listing to try again.");
            if (fail != null)
            {
                await unitOfWork.RollbackTransactionAsync();
                return fail;
            }

            var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Plot);
            var live = await slots.CountLiveAsync(userId, ListingKinds.Plot, plot!.Id);
            if (live >= limits.ListingLimit)
            {
                await unitOfWork.RollbackTransactionAsync();
                return PlanLimitReached(limits.ListingLimit, live);
            }

            var validUntil = entitlements.ResolveValidUntil(limits);
            submitted = plot.LiveRequestStatus != GoLiveRequestStatuses.Approved;
            if (submitted)
            {
                plot.LiveRequestStatus = GoLiveRequestStatuses.Pending;
                plot.RequestedPlanDays = DaysUntil(validUntil);
            }
            else
            {
                plot.IsActive = true;
                plot.ValidUntil = validUntil;
            }
            plot.PauseReason = null;
            plot.PriorityRank = limits.PriorityRank;
            plot.IsFeatured = limits.IsFeatured;
            plot.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.PlotListings.UpdateAsync(plot);
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse("This plot was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
            await unitOfWork.CommitTransactionAsync();

            districtId = plot.DistrictId;
            result = new GoLiveResultDto
            {
                ValidUntil = submitted ? null : plot.ValidUntil,
                LiveRequestStatus = plot.LiveRequestStatus,
                Limits = new LiveLimitsDto { ListingLimit = limits.ListingLimit, Live = live + 1 },
            };
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        if (submitted) await PublishGoLiveRequestedAsync(publisher, plotId, ListingKinds.Plot);
        else await cacheInvalidator.BustDistrictsAsync(ListingKinds.Plot, [districtId]);
        return OkResponse(result);
    }
}

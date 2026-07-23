using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Replaces PaymentService's membership-granting methods for both listing kinds. One thin handler per
// kind (each looks up its own listing type + plan type), both calling the exact same shared
// ICoinWalletService.SpendCoinsAsync — the coin-spend mechanism itself is never duplicated.
public static class GoLiveHandlers
{
    private static string RoomNearbyPattern(Guid districtId) => $"nearby:{districtId}:*";
    private static string PlotNearbyPattern(Guid districtId) => $"nearby_plot:{districtId}:*";

    private static async Task InvalidateCacheAsync(IConnectionMultiplexer? redis, string pattern)
    {
        if (redis == null) return;
        try
        {
            var db = redis.GetDatabase();
            var server = redis.GetServers().FirstOrDefault(s => s.IsConnected);
            if (server == null) return;
            await foreach (var key in server.KeysAsync(pattern: pattern))
                await db.KeyDeleteAsync(key);
        }
        catch { }
    }

    // Home's "Recently added" feed is cached under one fixed key per kind, separate from the
    // district-scoped nearby cache above — a listing going live can enter the top of that feed, so
    // bust it here rather than waiting out the TTL.
    private static async Task InvalidateRecentRoomsCacheAsync(IConnectionMultiplexer? redis)
    {
        if (redis == null) return;
        try { await redis.GetDatabase().KeyDeleteAsync("home:recentRooms"); } catch { }
    }

    private static async Task InvalidateRecentPlotsCacheAsync(IConnectionMultiplexer? redis)
    {
        if (redis == null) return;
        try { await redis.GetDatabase().KeyDeleteAsync("home:recentPlots"); } catch { }
    }

    // Home's "X for you" feed is cached per-district — a listing going live can enter the top of
    // that district's list too, so bust it here alongside the global "recently added" cache above.
    private static async Task InvalidateForYouRoomsCacheAsync(IConnectionMultiplexer? redis, Guid districtId)
    {
        if (redis == null) return;
        try { await redis.GetDatabase().KeyDeleteAsync($"home:forYouRooms:{districtId}"); } catch { }
    }

    private static async Task InvalidateForYouPlotsCacheAsync(IConnectionMultiplexer? redis, Guid districtId)
    {
        if (redis == null) return;
        try { await redis.GetDatabase().KeyDeleteAsync($"home:forYouPlots:{districtId}"); } catch { }
    }

    // Best-effort — a SignalR push failure must never turn an already-committed coin spend into an
    // error response. Only called from a point where the caller's own commit is already final.
    private static async Task PushWalletBalanceChangedAsync(IHubContext<WalletHub> hubContext, Guid userId, int balance, string reason)
    {
        try
        {
            await hubContext.Clients.Group($"user_{userId}").SendAsync("WalletBalanceChanged", new
            {
                balance,
                reason,
                occurredAt = DateTime.UtcNow,
            });
        }
        catch { }
    }

    public static async Task<IResult> GoLiveRoom(
        Guid listingId,
        GoLiveRequest request,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        ICoinWalletService wallet,
        IRateLimitService rateLimiter,
        IServiceProvider sp,
        IHubContext<WalletHub> hubContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var rl = await rateLimiter.CheckAsync($"golive:room:{listingId}", maxAttempts: 1, window: TimeSpan.FromSeconds(5));
        if (!rl.IsAllowed)
            return TooManyRequestsResponse();

        var listing = await unitOfWork.RoomListings.GetByIdAsync(listingId);
        if (listing == null || listing.IsDeleted) return NotFoundResponse("RoomListing not found");
        if (listing.UserId != userId) return ForbiddenResponse("You do not own this listing");

        var stillWithinValidity = listing.ValidUntil.HasValue && listing.ValidUntil > DateTime.UtcNow;
        if (listing.IsActive && stillWithinValidity)
            return BadRequestResponse("This listing is already live.");

        if (!listing.IsActive && stillWithinValidity)
        {
            // Free reactivation — already paid for this window (owner deactivated manually, then
            // came back before it expired). PlanType is never required on this branch.
            listing.IsActive = true;
            listing.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.RoomListings.UpdateAsync(listing);
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return ConflictResponse("This listing was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
            await InvalidateCacheAsync(sp.GetService<IConnectionMultiplexer>(), RoomNearbyPattern(listing.DistrictId));
            await InvalidateRecentRoomsCacheAsync(sp.GetService<IConnectionMultiplexer>());
            await InvalidateForYouRoomsCacheAsync(sp.GetService<IConnectionMultiplexer>(), listing.DistrictId);
            return OkResponse(new
            {
                success = true,
                isActive = true,
                validUntil = listing.ValidUntil,
                planType = (string?)null,
                balance = await wallet.GetBalanceAsync(userId),
            });
        }

        if (string.IsNullOrWhiteSpace(request.PlanType))
            return BadRequestResponse("PlanType is required to go live on an expired or never-activated listing.");

        var plan = await unitOfWork.CoinPlans.GetByFeatureKeyAndPlanTypeAsync(CoinFeatureKeys.RoomGoLive, request.PlanType.Trim().ToUpperInvariant());
        if (plan == null || !plan.IsEnabled)
            return BadRequestResponse("Plan not found or disabled");

        await unitOfWork.BeginTransactionAsync();
        try
        {
            var spend = await wallet.SpendCoinsAsync(userId, plan.OriginalPrice, CoinTransactionReasons.RoomGoLive, listingId);
            if (spend.Outcome != CoinSpendOutcome.Success)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse(
                    $"Insufficient balance: this plan costs {plan.OriginalPrice} coins, you have {spend.BalanceAfter}.",
                    "INSUFFICIENT_BALANCE");
            }

            listing.IsActive = true;
            listing.ValidUntil = DateTime.UtcNow.AddDays(plan.Days);
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
            await PushWalletBalanceChangedAsync(hubContext, userId, spend.BalanceAfter, CoinTransactionReasons.RoomGoLive);
            await InvalidateCacheAsync(sp.GetService<IConnectionMultiplexer>(), RoomNearbyPattern(listing.DistrictId));
            await InvalidateRecentRoomsCacheAsync(sp.GetService<IConnectionMultiplexer>());
            await InvalidateForYouRoomsCacheAsync(sp.GetService<IConnectionMultiplexer>(), listing.DistrictId);

            return OkResponse(new
            {
                success = true,
                isActive = true,
                validUntil = listing.ValidUntil,
                planType = plan.PlanType,
                balance = spend.BalanceAfter,
            });
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    public static async Task<IResult> GoLivePlot(
        Guid plotId,
        GoLiveRequest request,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        ICoinWalletService wallet,
        IRateLimitService rateLimiter,
        IServiceProvider sp,
        IHubContext<WalletHub> hubContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var rl = await rateLimiter.CheckAsync($"golive:plot:{plotId}", maxAttempts: 1, window: TimeSpan.FromSeconds(5));
        if (!rl.IsAllowed)
            return TooManyRequestsResponse();

        var plot = await unitOfWork.PlotListings.GetByIdAsync(plotId);
        if (plot == null || plot.IsDeleted) return NotFoundResponse("PlotListing not found");
        if (plot.UserId != userId) return ForbiddenResponse("You do not own this plot");

        var stillWithinValidity = plot.ValidUntil.HasValue && plot.ValidUntil > DateTime.UtcNow;
        if (plot.IsActive && stillWithinValidity)
            return BadRequestResponse("This plot is already live.");

        if (!plot.IsActive && stillWithinValidity)
        {
            plot.IsActive = true;
            plot.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.PlotListings.UpdateAsync(plot);
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return ConflictResponse("This listing was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
            await InvalidateCacheAsync(sp.GetService<IConnectionMultiplexer>(), PlotNearbyPattern(plot.DistrictId));
            await InvalidateRecentPlotsCacheAsync(sp.GetService<IConnectionMultiplexer>());
            await InvalidateForYouPlotsCacheAsync(sp.GetService<IConnectionMultiplexer>(), plot.DistrictId);
            return OkResponse(new
            {
                success = true,
                isActive = true,
                validUntil = plot.ValidUntil,
                planType = (string?)null,
                balance = await wallet.GetBalanceAsync(userId),
            });
        }

        if (string.IsNullOrWhiteSpace(request.PlanType))
            return BadRequestResponse("PlanType is required to go live on an expired or never-activated plot.");

        var plan = await unitOfWork.CoinPlans.GetByFeatureKeyAndPlanTypeAsync(CoinFeatureKeys.PlotGoLive, request.PlanType.Trim().ToUpperInvariant());
        if (plan == null || !plan.IsEnabled)
            return BadRequestResponse("Plan not found or disabled");

        await unitOfWork.BeginTransactionAsync();
        try
        {
            var spend = await wallet.SpendCoinsAsync(userId, plan.OriginalPrice, CoinTransactionReasons.PlotGoLive, plotId);
            if (spend.Outcome != CoinSpendOutcome.Success)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse(
                    $"Insufficient balance: this plan costs {plan.OriginalPrice} coins, you have {spend.BalanceAfter}.",
                    "INSUFFICIENT_BALANCE");
            }

            plot.IsActive = true;
            plot.ValidUntil = DateTime.UtcNow.AddDays(plan.Days);
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
            await PushWalletBalanceChangedAsync(hubContext, userId, spend.BalanceAfter, CoinTransactionReasons.PlotGoLive);
            await InvalidateCacheAsync(sp.GetService<IConnectionMultiplexer>(), PlotNearbyPattern(plot.DistrictId));
            await InvalidateRecentPlotsCacheAsync(sp.GetService<IConnectionMultiplexer>());
            await InvalidateForYouPlotsCacheAsync(sp.GetService<IConnectionMultiplexer>(), plot.DistrictId);

            return OkResponse(new
            {
                success = true,
                isActive = true,
                validUntil = plot.ValidUntil,
                planType = plan.PlanType,
                balance = spend.BalanceAfter,
            });
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }
}

public record GoLiveRequest(string? PlanType);

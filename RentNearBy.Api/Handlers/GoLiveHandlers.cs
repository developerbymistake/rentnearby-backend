using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Replaces PaymentService's membership-granting methods for both listing kinds. One thin handler per
// kind (each looks up its own listing type + plan type), both calling the exact same shared
// ICreditWalletService.SpendCreditsAsync — the credit-spend mechanism itself is never duplicated.
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

    // Best-effort — a SignalR push failure must never turn an already-committed credit spend into an
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

    // Best-effort — mirrors ListingsHandlers/PlotHandlers' report.filed publish shape exactly. A
    // publish failure must never turn an already-committed Pending request into an error response.
    // Only called from a point where the caller's own commit (or SaveChangesAsync, for the free
    // case) is already final.
    private static async Task PublishGoLiveRequestedAsync(IRabbitMqPublisher publisher, Guid listingId, string listingType)
    {
        try
        {
            var message = new GoLiveRequestedMessage { ListingId = listingId, ListingType = listingType };
            await publisher.PublishAsync("golive.requested", JsonSerializer.Serialize(message));
        }
        catch { }
    }

    public static async Task<IResult> GoLiveRoom(
        Guid listingId,
        GoLiveRequest request,
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        ICreditWalletService wallet,
        IRateLimitService rateLimiter,
        IServiceProvider sp,
        IHubContext<WalletHub> hubContext,
        IMemoryCache cache,
        IRabbitMqPublisher publisher)
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

        // "Needs a fresh Go-Live" (today's branches 2/3) is no longer decided purely by
        // IsActive/ValidUntil — it must also ask "has this listing ever been approved before?"
        // If yes, it was already vetted once and is only expiring/reactivating again, so it must
        // never re-enter moderation: skip Pending entirely and fall straight into today's existing
        // immediate-activation logic, unchanged. Only a genuinely first-ever Go-Live attempt
        // (LiveRequestStatus not Approved — i.e. null, since Rejected has no resubmit path) takes
        // the new Pending path below.
        var alreadyApproved = listing.LiveRequestStatus == GoLiveRequestStatuses.Approved;

        var (paymentEnabled, freeDays) = await ConfigHandlers.GetPaymentFeatureCachedAsync(unitOfWork, cache);
        if (!paymentEnabled)
        {
            if (alreadyApproved)
            {
                // Payment kill switch is OFF — Go-Live is free for the admin-configured number of
                // days instead of requiring a plan/spend. Mirrors the free-reactivation branch's
                // response shape. Exactly today's existing behavior, unchanged.
                listing.IsActive = true;
                listing.ValidUntil = DateTime.UtcNow.AddDays(freeDays);
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

            // New path: first-ever Go-Live, free mode — submit for moderation instead of activating.
            // Nothing charged, so no transaction/wallet call needed.
            listing.LiveRequestStatus = GoLiveRequestStatuses.Pending;
            listing.RequestedPlanType = null;
            listing.RequestedPlanDays = freeDays;
            listing.RequestedPlanCreditsSpent = 0;
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
            await PublishGoLiveRequestedAsync(publisher, listing.Id, "Room");
            return OkResponse(new
            {
                success = true,
                isActive = false,
                status = GoLiveRequestStatuses.Pending,
                validUntil = (DateTime?)null,
                planType = (string?)null,
                creditsSpent = 0,
                balance = await wallet.GetBalanceAsync(userId),
            });
        }

        if (string.IsNullOrWhiteSpace(request.PlanType))
            return BadRequestResponse("PlanType is required to go live on an expired or never-activated listing.");

        var plan = await unitOfWork.CreditPlans.GetByFeatureKeyAndPlanTypeAsync(CreditFeatureKeys.RoomGoLive, request.PlanType.Trim().ToUpperInvariant());
        if (plan == null || !plan.IsEnabled)
            return BadRequestResponse("Plan not found or disabled");

        await unitOfWork.BeginTransactionAsync();
        try
        {
            var spend = await wallet.SpendCreditsAsync(userId, plan.OriginalPrice, CreditTransactionReasons.RoomGoLive, listingId);
            if (spend.Outcome != CreditSpendOutcome.Success)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse(
                    $"Insufficient balance: this plan costs {plan.OriginalPrice} credits, you have {spend.BalanceAfter}.",
                    "INSUFFICIENT_BALANCE");
            }

            if (alreadyApproved)
            {
                // Exactly today's existing behavior, unchanged — this listing was vetted before.
                listing.IsActive = true;
                listing.ValidUntil = DateTime.UtcNow.AddDays(plan.Days);
            }
            else
            {
                // New path: first-ever Go-Live, paid mode — credits are still spent immediately
                // (unchanged UX), but the listing goes Pending instead of activating. Snapshot the
                // plan now so a later approval computes ValidUntil from this snapshot, never by
                // re-resolving CreditPlan (which is admin-mutable and could change/be disabled
                // between request and approval).
                listing.LiveRequestStatus = GoLiveRequestStatuses.Pending;
                listing.RequestedPlanType = plan.PlanType;
                listing.RequestedPlanDays = plan.Days;
                listing.RequestedPlanCreditsSpent = plan.OriginalPrice;
            }
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
            await PushWalletBalanceChangedAsync(hubContext, userId, spend.BalanceAfter, CreditTransactionReasons.RoomGoLive);

            if (alreadyApproved)
            {
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

            await PublishGoLiveRequestedAsync(publisher, listing.Id, "Room");
            return OkResponse(new
            {
                success = true,
                isActive = false,
                status = GoLiveRequestStatuses.Pending,
                validUntil = (DateTime?)null,
                planType = plan.PlanType,
                creditsSpent = plan.OriginalPrice,
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
        ICreditWalletService wallet,
        IRateLimitService rateLimiter,
        IServiceProvider sp,
        IHubContext<WalletHub> hubContext,
        IMemoryCache cache,
        IRabbitMqPublisher publisher)
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

        // "Needs a fresh Go-Live" (today's branches 2/3) is no longer decided purely by
        // IsActive/ValidUntil — it must also ask "has this plot ever been approved before?" If
        // yes, it was already vetted once and is only expiring/reactivating again, so it must
        // never re-enter moderation: skip Pending entirely and fall straight into today's existing
        // immediate-activation logic, unchanged. Only a genuinely first-ever Go-Live attempt
        // (LiveRequestStatus not Approved — i.e. null, since Rejected has no resubmit path) takes
        // the new Pending path below.
        var alreadyApproved = plot.LiveRequestStatus == GoLiveRequestStatuses.Approved;

        var (paymentEnabled, freeDays) = await ConfigHandlers.GetPaymentFeatureCachedAsync(unitOfWork, cache);
        if (!paymentEnabled)
        {
            if (alreadyApproved)
            {
                // Payment kill switch is OFF — Go-Live is free for the admin-configured number of
                // days instead of requiring a plan/spend. Mirrors the free-reactivation branch's
                // response shape. Exactly today's existing behavior, unchanged.
                plot.IsActive = true;
                plot.ValidUntil = DateTime.UtcNow.AddDays(freeDays);
                plot.UpdatedAt = DateTime.UtcNow;
                await unitOfWork.PlotListings.UpdateAsync(plot);
                try
                {
                    await unitOfWork.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    return ConflictResponse("This plot was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
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

            // New path: first-ever Go-Live, free mode — submit for moderation instead of activating.
            // Nothing charged, so no transaction/wallet call needed.
            plot.LiveRequestStatus = GoLiveRequestStatuses.Pending;
            plot.RequestedPlanType = null;
            plot.RequestedPlanDays = freeDays;
            plot.RequestedPlanCreditsSpent = 0;
            plot.UpdatedAt = DateTime.UtcNow;
            await unitOfWork.PlotListings.UpdateAsync(plot);
            try
            {
                await unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return ConflictResponse("This plot was just modified by another request. Please retry.", "CONCURRENT_UPDATE");
            }
            await PublishGoLiveRequestedAsync(publisher, plot.Id, "Plot");
            return OkResponse(new
            {
                success = true,
                isActive = false,
                status = GoLiveRequestStatuses.Pending,
                validUntil = (DateTime?)null,
                planType = (string?)null,
                creditsSpent = 0,
                balance = await wallet.GetBalanceAsync(userId),
            });
        }

        if (string.IsNullOrWhiteSpace(request.PlanType))
            return BadRequestResponse("PlanType is required to go live on an expired or never-activated plot.");

        var plan = await unitOfWork.CreditPlans.GetByFeatureKeyAndPlanTypeAsync(CreditFeatureKeys.PlotGoLive, request.PlanType.Trim().ToUpperInvariant());
        if (plan == null || !plan.IsEnabled)
            return BadRequestResponse("Plan not found or disabled");

        await unitOfWork.BeginTransactionAsync();
        try
        {
            var spend = await wallet.SpendCreditsAsync(userId, plan.OriginalPrice, CreditTransactionReasons.PlotGoLive, plotId);
            if (spend.Outcome != CreditSpendOutcome.Success)
            {
                await unitOfWork.RollbackTransactionAsync();
                return ConflictResponse(
                    $"Insufficient balance: this plan costs {plan.OriginalPrice} credits, you have {spend.BalanceAfter}.",
                    "INSUFFICIENT_BALANCE");
            }

            if (alreadyApproved)
            {
                // Exactly today's existing behavior, unchanged — this plot was vetted before.
                plot.IsActive = true;
                plot.ValidUntil = DateTime.UtcNow.AddDays(plan.Days);
            }
            else
            {
                // New path: first-ever Go-Live, paid mode — credits are still spent immediately
                // (unchanged UX), but the plot goes Pending instead of activating. Snapshot the
                // plan now so a later approval computes ValidUntil from this snapshot, never by
                // re-resolving CreditPlan (which is admin-mutable and could change/be disabled
                // between request and approval).
                plot.LiveRequestStatus = GoLiveRequestStatuses.Pending;
                plot.RequestedPlanType = plan.PlanType;
                plot.RequestedPlanDays = plan.Days;
                plot.RequestedPlanCreditsSpent = plan.OriginalPrice;
            }
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
            await PushWalletBalanceChangedAsync(hubContext, userId, spend.BalanceAfter, CreditTransactionReasons.PlotGoLive);

            if (alreadyApproved)
            {
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

            await PublishGoLiveRequestedAsync(publisher, plot.Id, "Plot");
            return OkResponse(new
            {
                success = true,
                isActive = false,
                status = GoLiveRequestStatuses.Pending,
                validUntil = (DateTime?)null,
                planType = plan.PlanType,
                creditsSpent = plan.OriginalPrice,
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

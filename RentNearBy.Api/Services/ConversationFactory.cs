using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Api.Services;

public sealed class ConversationFactory(
    IUnitOfWork unitOfWork, IRateLimitService rateLimiter, ApplicationDbContext db, IMemoryCache cache) : IConversationFactory
{
    private const int NewConversationMaxPerDay = 10;
    private static readonly TimeSpan NewConversationWindow = TimeSpan.FromHours(24);
    // TEMPORARY for local dev/testing — revert to TimeSpan.FromHours(1) before shipping.
    private static readonly TimeSpan NewAccountCooldown = TimeSpan.FromMinutes(5);

    public async Task<ConversationFactoryResult> GetOrCreateAsync(
        Guid callerId, string listingType, Guid listingId, bool skipRateLimit = false, bool requireActive = true)
    {
        // Existing conversations stay readable; only starting one from an off tab's listing is refused.
        if (!TabState.IsKindActive(await ConfigHandlers.GetAppTabsCachedAsync(unitOfWork, cache), listingType))
            return Fail(403, "This feature is currently unavailable.", TabGateFilter.ErrorType);

        var caller = await unitOfWork.Users.GetByIdAsync(callerId);
        if (caller == null) return Fail(404, "User not found", "NotFound");

        var accountAge = DateTime.UtcNow - caller.CreatedAt;
        if (accountAge < NewAccountCooldown)
        {
            var remainingMinutes = Math.Max(1, (int)Math.Ceiling((NewAccountCooldown - accountAge).TotalMinutes));
            return Fail(403, $"You have a new account, so you can't chat yet. Try again in {remainingMinutes} min.", "Forbidden");
        }

        Guid? ownerId;
        bool? listingIsActive;
        if (listingType == "Room")
        {
            var l = await db.RoomListings.Where(x => x.Id == listingId && !x.IsDeleted)
                .Select(x => new { x.UserId, x.IsActive }).FirstOrDefaultAsync();
            ownerId = l?.UserId; listingIsActive = l?.IsActive;
        }
        else if (listingType == ListingKinds.Stay)
        {
            var l = await db.StayListings.Where(x => x.Id == listingId && !x.IsDeleted)
                .Select(x => new { x.UserId, IsActive = x.Status == StayStatuses.Approved && x.AcceptingGuests }).FirstOrDefaultAsync();
            ownerId = l?.UserId; listingIsActive = l?.IsActive;
        }
        else
        {
            var l = await db.PlotListings.Where(x => x.Id == listingId && !x.IsDeleted)
                .Select(x => new { x.UserId, x.IsActive }).FirstOrDefaultAsync();
            ownerId = l?.UserId; listingIsActive = l?.IsActive;
        }

        if (ownerId == null) return Fail(404, "Listing not found", "NotFound");
        if (ownerId == callerId) return Fail(400, "You can't start a chat about your own listing", "BadRequest");

        if (await unitOfWork.UserBlocks.ExistsAsync(ownerId.Value, callerId) ||
            await unitOfWork.UserBlocks.ExistsAsync(callerId, ownerId.Value))
            return Fail(403, "You can't message this user", "Forbidden");

        var existing = await unitOfWork.Conversations.FindExistingAsync(callerId, ownerId.Value, listingType, listingId);
        if (existing != null)
        {
            await ChatHandlers.RefreshListingLifecycleStatusAsync(existing, db, unitOfWork);
            await unitOfWork.SaveChangesAsync();
            return new ConversationFactoryResult(existing, false, 200, null, null);
        }

        if (requireActive && listingIsActive == false)
            return Fail(409, "This listing is not available", "ListingNotAvailable");

        if (!skipRateLimit)
        {
            var rl = await rateLimiter.CheckAsync($"chat:newconv:{callerId}", NewConversationMaxPerDay, NewConversationWindow);
            if (!rl.IsAllowed)
                return new ConversationFactoryResult(null, false, 429, "Too many requests. Please try again later.", "TooManyRequests",
                    (int?)rl.RetryAfter?.TotalSeconds);
        }

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            RenterId = callerId,
            OwnerId = ownerId.Value,
            ListingType = listingType,
            ListingId = listingId,
            Status = ChatHandlers.ComputeLifecycleStatus(isDeleted: false, isActive: listingIsActive),
            CreatedAt = DateTime.UtcNow,
            LastMessageAt = DateTime.UtcNow,
        };

        await unitOfWork.Conversations.AddAsync(conversation);
        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost a race on the unique renter+owner+listing index. The failed insert stays tracked as Added, so detach it
            // (callers may keep saving on this context inside their transaction) and return the winning row.
            db.Entry(conversation).State = EntityState.Detached;
            var winner = await unitOfWork.Conversations.FindExistingAsync(callerId, ownerId.Value, listingType, listingId);
            if (winner != null) return new ConversationFactoryResult(winner, false, 200, null, null);
            throw;
        }

        return new ConversationFactoryResult(conversation, true, 201, null, null);
    }

    private static ConversationFactoryResult Fail(int status, string message, string type) =>
        new(null, false, status, message, type);
}

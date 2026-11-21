using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Infrastructure.Services;

// A push decided inside a transaction and built + sent after commit.
public sealed record TripPendingPush(
    TripEnquiry Enquiry, Guid? RecipientUserId, Func<TripEnquiry, TripPushContext, NotificationEvent> Build,
    int? Total = null, string? DedupeKey = null);

public static class TripPushSender
{
    // After commit only; never throws.
    public static async Task SendAsync(
        IReadOnlyList<TripPendingPush> pushes, ITripEnquiryRepository enquiries, ITripNotifier notifier, ILogger logger)
    {
        foreach (var p in pushes)
        {
            if (p.RecipientUserId is not { } recipient) continue;
            try
            {
                var ctx = await enquiries.LoadPushContextAsync(p.Enquiry, recipient, p.Total);
                var n = p.Build(p.Enquiry, ctx);
                if (p.DedupeKey != null)
                    await notifier.SendOnceAsync(n, p.DedupeKey, p.Enquiry.TripId, p.Enquiry.Id);
                else
                    await notifier.SendAsync(n);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "trip.push.failed {EnquiryId}", p.Enquiry.Id);
            }
        }
    }
}

// Shared state-change steps used by the handlers, the closure and the timer. All run inside the caller's transaction.
public static class TripEnquiryOps
{
    // The pending offer of the enquiry (if any) moves to offerStatus and its chat card to cardStatus.
    public static async Task CloseOpenOfferAsync(
        ApplicationDbContext db, ITripChatService chat, TripEnquiry e, string offerStatus, string cardStatus,
        List<TripChatPost> posts, DateTime nowUtc)
    {
        var offers = await db.TripOffers
            .Where(o => o.TripEnquiryId == e.Id && o.Status == TripOfferStatuses.Pending)
            .ToListAsync();
        foreach (var offer in offers)
        {
            offer.Status = offerStatus;
            offer.RespondedAt ??= nowUtc;
            if (offer.MessageId is { } messageId && await chat.RewriteStatusAsync(messageId, cardStatus) is { } post)
                posts.Add(post);
        }
    }

    public static async Task PostSystemAsync(
        ApplicationDbContext db, ITripChatService chat, TripEnquiry e, Guid? actorUserId, string kind, List<TripChatPost> posts)
    {
        if (e.ConversationId is not { } conversationId) return;
        var actor = actorUserId ?? e.TravellerUserId ?? e.OwnerUserId;
        if (actor is not { } actorId) return;
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
        if (conversation == null) return;
        posts.Add(await chat.PostSystemAsync(conversation, actorId, kind, TripSystemLines.Text(kind)));
    }
}

// Scoped: keeps the chat posts and the taxi request signals of its own On* calls until AfterCommitAsync.
// Every entry point also runs the taxi request closure (MASTER_PLAN B2-8), so each existing caller covers both.
public class TripEnquiryClosure(
    ApplicationDbContext db, IUnitOfWork unitOfWork, ITripChatService chat, ITripNotifier notifier,
    ILogger<TripEnquiryClosure> logger, TaxiRequestClosure? taxiRequests = null) : ITripEnquiryClosure
{
    private readonly List<TripChatPost> _posts = new();
    private readonly List<TaxiClosureResult> _taxi = new();
    private bool _taxiPoolChanged;

    private ITripEnquiryRepository Enquiries => unitOfWork.TripEnquiries;

    // False only when built without DI (test harness); the DI resolution test pins it true for the app container.
    public bool RunsTaxiClosure => taxiRequests is not null;

    // Taxi requests first: they are locked before the first save here can flush a caller's pending listing write (2.4).
    public async Task<IReadOnlyList<TripClosurePush>> OnListingsRemovedAsync(string kind, IReadOnlyCollection<Guid> listingIds)
    {
        if (kind == ListingKinds.Taxi)
            foreach (var id in listingIds.Distinct().Order())
                await RunTaxiAsync(t => t.ListingRemovedAsync(db, id), poolChanged: true);
        return await CloseEnquiriesOfRemovedListingsAsync(kind, listingIds);
    }

    private async Task<IReadOnlyList<TripClosurePush>> CloseEnquiriesOfRemovedListingsAsync(string kind, IReadOnlyCollection<Guid> listingIds)
    {
        if (listingIds.Count == 0) return [];
        var ids = listingIds.Distinct().ToList();
        // A removed listing leaves the traveller's shortlist and the morning reminders (same transaction as the removal).
        var shortlistKinds = kind == ListingKinds.Stay ? new[] { TripShortlistKinds.Stay } : new[] { TripShortlistKinds.Taxi, TripShortlistKinds.Car };
        await CloseShortlistAsync(i => shortlistKinds.Contains(i.Kind) && ids.Contains(i.ListingId));
        var rows = await db.TripEnquiries
            .Where(e => e.Kind == kind && ids.Contains(e.ListingId) && TripEnquiryStatuses.Open.Contains(e.Status))
            .ToListAsync();
        if (rows.Count == 0) return [];

        var now = DateTime.UtcNow;
        var pushes = new List<TripClosurePush>();
        foreach (var e in rows)
        {
            if (TripEnquiryStatuses.IsBooked(e.Status))
            {
                e.Status = TripEnquiryStatuses.CancelledByOwner;
                e.CancelledAt = now;
                e.CancelledByUserId = e.OwnerUserId;
            }
            else
            {
                e.Status = TripEnquiryStatuses.NotAvailable;
            }
            e.FailReason = TripFailReasons.ListingRemoved;
            e.UpdatedAt = now;
            await TripEnquiryOps.CloseOpenOfferAsync(db, chat, e, TripOfferStatuses.Superseded, TripCardStatuses.Superseded, _posts, now);
            await TripEnquiryOps.PostSystemAsync(db, chat, e, e.OwnerUserId, TripSystemLines.ListingRemoved, _posts);
            await JourneyLegSync.SyncAsync(db, e, now);
            if (e.TravellerUserId is { } travellerId)
                pushes.Add(await BuildAsync(e, travellerId, TripNotifications.TripListingRemoved, $"gone:{e.Id}", e.TripId));
        }
        await db.SaveChangesAsync();
        return pushes;
    }

    public async Task<IReadOnlyList<TripClosurePush>> OnTripDeletedAsync(Guid tripId)
    {
        await RunTaxiAsync(t => t.TripDeletedAsync(db, tripId));
        return await CloseEnquiriesOfDeletedTripAsync(tripId);
    }

    private async Task<IReadOnlyList<TripClosurePush>> CloseEnquiriesOfDeletedTripAsync(Guid tripId)
    {
        var rows = await db.TripEnquiries.Where(e => e.TripId == tripId).ToListAsync();
        if (rows.Count == 0) return [];

        var now = DateTime.UtcNow;
        var pushes = new List<TripClosurePush>();
        foreach (var e in rows)
        {
            if (TripEnquiryStatuses.IsOpen(e.Status))
                pushes.AddRange(await CloseForLeavingTravellerAsync(e, TripCloseReasons.TripDeleted, now));
            // Nulled here (not by the FK) so tracked rows never race the cascade on the trip delete.
            e.TripId = null;
            e.UpdatedAt = now;
        }
        await db.SaveChangesAsync();
        return pushes;
    }

    public async Task<IReadOnlyList<TripClosurePush>> OnTravellerDeletedAsync(Guid userId)
    {
        var pushes = await CloseEnquiriesOfDeletedTravellerAsync(userId);
        await RunTaxiAsync(t => t.TravellerAccountDeletedAsync(db, userId));
        return pushes;
    }

    private async Task<IReadOnlyList<TripClosurePush>> CloseEnquiriesOfDeletedTravellerAsync(Guid userId)
    {
        var rows = await db.TripEnquiries
            .Where(e => e.TravellerUserId == userId && TripEnquiryStatuses.Open.Contains(e.Status))
            .ToListAsync();
        if (rows.Count == 0) return [];

        var now = DateTime.UtcNow;
        var pushes = new List<TripClosurePush>();
        foreach (var e in rows)
            pushes.AddRange(await CloseForLeavingTravellerAsync(e, TripCloseReasons.AccountDeleted, now));
        await db.SaveChangesAsync();
        return pushes;
    }

    // The block route calls this outside a transaction; the taxi part then gets its own, so its locks and rows go together.
    public async Task OnBlockAsync(Guid userA, Guid userB)
    {
        await CloseEnquiriesOfBlockAsync(userA, userB);
        if (taxiRequests is null) return;
        if (db.Database.CurrentTransaction is not null)
        {
            await RunTaxiAsync(t => t.UsersBlockedAsync(db, userA, userB));
            return;
        }
        await using var tx = await db.Database.BeginTransactionAsync();
        await RunTaxiAsync(t => t.UsersBlockedAsync(db, userA, userB));
        await tx.CommitAsync();
    }

    private async Task CloseEnquiriesOfBlockAsync(Guid userA, Guid userB)
    {
        var rows = await db.TripEnquiries
            .Where(e => TripEnquiryStatuses.Withdrawable.Contains(e.Status)
                && ((e.TravellerUserId == userA && e.OwnerUserId == userB) || (e.TravellerUserId == userB && e.OwnerUserId == userA)))
            .ToListAsync();
        if (rows.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var e in rows)
        {
            e.Status = TripEnquiryStatuses.NotAvailable;
            e.UpdatedAt = now;
            await TripEnquiryOps.CloseOpenOfferAsync(db, chat, e, TripOfferStatuses.Superseded, TripCardStatuses.Superseded, _posts, now);
        }
        await db.SaveChangesAsync();
    }

    // Local sightseeing leads only (trip row, no travel day): journey cars and trip-less journey leads ride a route, not the day rate.
    public async Task<IReadOnlyList<TripClosurePush>> OnTaxiRateClearedAsync(Guid taxiId)
    {
        await CloseShortlistAsync(i => i.Kind == TripShortlistKinds.Taxi && i.ListingId == taxiId);
        var rows = await db.TripEnquiries
            .Where(TripSegmentRows.LocalTaxi)
            .Where(e => e.ListingId == taxiId && (e.Status == TripEnquiryStatuses.Asked || e.Status == TripEnquiryStatuses.FareSent))
            .ToListAsync();
        if (rows.Count == 0) return [];

        var now = DateTime.UtcNow;
        var pushes = new List<TripClosurePush>();
        foreach (var e in rows)
        {
            e.Status = TripEnquiryStatuses.NotAvailable;
            e.FailReason = null;
            e.UpdatedAt = now;
            await JourneyLegSync.SyncAsync(db, e, now);
            if (e.TravellerUserId is { } travellerId)
                pushes.Add(await BuildAsync(e, travellerId, TripNotifications.TripNotAvailable, $"gone-rate:{e.Id}", e.TripId));
        }
        await db.SaveChangesAsync();
        return pushes;
    }

    private async Task CloseShortlistAsync(System.Linq.Expressions.Expression<Func<TripShortlistItem, bool>> match)
    {
        var now = DateTime.UtcNow;
        await db.TripShortlistItems
            .Where(i => i.State == TripShortlistStates.Active)
            .Where(match)
            .ExecuteUpdateAsync(set => set
                .SetProperty(i => i.State, TripShortlistStates.Closed)
                .SetProperty(i => i.ClosedReason, TripShortlistClosedReasons.Unavailable)
                .SetProperty(i => i.ClosedAt, now));
    }

    public async Task AfterCommitAsync(IReadOnlyList<TripClosurePush> pushes)
    {
        var posts = _posts.ToList();
        _posts.Clear();
        try { await chat.AfterCommitAsync(posts); }
        catch (Exception ex) { logger.LogError(ex, "trip.closure.broadcast_failed"); }

        foreach (var p in pushes)
        {
            try { await notifier.SendOnceAsync(p.Notification, p.DedupeKey, p.TripId, p.EnquiryId); }
            catch (Exception ex) { logger.LogError(ex, "trip.closure.push_failed {EnquiryId}", p.EnquiryId); }
        }

        var taxi = TaxiRequestClosure.Merge(_taxi);
        var poolChanged = _taxiPoolChanged;
        _taxi.Clear();
        _taxiPoolChanged = false;
        if (taxiRequests is not null) await taxiRequests.AfterCommitAsync(taxi, poolChanged);
    }

    // Taxi request rows move in the caller's transaction; their push rows are saved here, the signals wait for AfterCommitAsync.
    private async Task RunTaxiAsync(Func<TaxiRequestClosure, Task<TaxiClosureResult>> step, bool poolChanged = false)
    {
        if (taxiRequests is null) return;
        var result = await step(taxiRequests);
        await db.SaveChangesAsync();
        _taxi.Add(result);
        _taxiPoolChanged |= poolChanged;
    }

    // Trip deleted or traveller account deleted: unconfirmed rows close, booked rows count as a guest cancel.
    private async Task<IReadOnlyList<TripClosurePush>> CloseForLeavingTravellerAsync(TripEnquiry e, string closeReason, DateTime now)
    {
        if (TripEnquiryStatuses.IsBooked(e.Status))
        {
            e.Status = TripEnquiryStatuses.CancelledByTraveller;
            e.CancelledAt = now;
            e.CancelledByUserId = e.TravellerUserId;
        }
        else
        {
            e.Status = TripEnquiryStatuses.Closed;
            e.FailReason = closeReason;
        }
        e.UpdatedAt = now;
        await TripEnquiryOps.CloseOpenOfferAsync(db, chat, e, TripOfferStatuses.Superseded, TripCardStatuses.Superseded, _posts, now);
        await TripEnquiryOps.PostSystemAsync(db, chat, e, e.TravellerUserId, TripSystemLines.TripDeleted, _posts);
        if (e.OwnerUserId is not { } ownerId) return [];
        // TripId null: the trip row (and with it any log row pointing at it) is gone after this transaction.
        return [await BuildAsync(e, ownerId, TripNotifications.TripTravellerLeft, $"left:{e.Id}", null)];
    }

    private async Task<TripClosurePush> BuildAsync(
        TripEnquiry e, Guid recipient, Func<TripEnquiry, TripPushContext, NotificationEvent> build, string key, Guid? tripId)
    {
        var ctx = await Enquiries.LoadPushContextAsync(e, recipient);
        return new TripClosurePush(build(e, ctx), key, tripId, e.Id);
    }
}

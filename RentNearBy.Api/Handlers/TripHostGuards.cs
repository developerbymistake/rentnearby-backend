using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Api.Handlers;

// Host-side gates of the trip journey (owner D): pure rules plus the admin take-down closure.
public static class TripHostGuards
{
    public const string OfferOutsideLegType = "OfferOutsideLeg";

    // The window or the check-in day is over: the host cannot answer even before the timer sweeps the row.
    public static bool EnquiryOver(string status, DateTime expiresAt, DateOnly? checkIn, DateTime nowUtc, DateOnly today)
        => (TripEnquiryStatuses.IsUnconfirmed(status) && expiresAt <= nowUtc) || (checkIn is { } c && c < today);

    // The counter is Accepted only by an offer with the same dates, total and rooms.
    public static bool CounterMatches(TripOffer counter, DateOnly checkIn, DateOnly checkOut, int total, IReadOnlyList<TripOfferLine> lines)
    {
        if (counter.Total != total || counter.CheckIn != checkIn || counter.CheckOut != checkOut) return false;
        static string Key(TripOfferLine l) => $"{(l.RoomName ?? "").Trim().ToLowerInvariant()}|{l.Units}|{l.PricePerNight}";
        var a = TripEnquiryDtoBuilder.ReadLines(counter.LinesJson).Select(Key).OrderBy(x => x, StringComparer.Ordinal);
        var b = lines.Select(Key).OrderBy(x => x, StringComparer.Ordinal);
        return a.SequenceEqual(b);
    }

    // Policy m12: the advance stays within the accepted offer's advance (a missing or zero one falls back to the agreed total).
    public static int MaxAdvance(int? agreedTotal, int? offerAdvance)
    {
        var cap = agreedTotal is > 0 ? agreedTotal.Value : TripExecutionRules.MaxAmount;
        return offerAdvance is > 0 ? Math.Min(cap, offerAdvance.Value) : cap;
    }

    // The offered lines carry the whole party and the rooms (where the room is known) hold it.
    public static bool PartyFits(IReadOnlyList<TripOfferLine> lines, int party, IReadOnlyDictionary<string, int>? roomCaps)
    {
        if (party <= 0) return true;
        if (lines.Sum(l => l.Guests) < party) return false;
        var seats = lines.Sum(l => roomCaps != null && l.RoomName != null && roomCaps.TryGetValue(l.RoomName.Trim(), out var cap)
            ? (long)l.Units * cap : l.Guests);
        return seats >= party;
    }

    // The nights sit inside the city (leg) of the enquiry's day; no plan or an empty leg means no check.
    public static bool OfferInsideLeg(TripPlan? plan, DateOnly start, int? enquiryDay, DateOnly checkIn, DateOnly checkOut)
    {
        if (plan == null || enquiryDay is not { } day || plan.Legs is not { Count: > 0 }) return true;
        var leg = plan.LegIndexOf(day);
        int first = plan.LegStart(leg), last = plan.LegEnd(leg);
        if (first == 0 || last == 0) return true;
        var firstNight = TripExecutionRules.DayOf(start, checkIn);
        var lastNight = TripExecutionRules.DayOf(start, checkOut) - 1;
        return firstNight >= first && lastNight <= last;
    }

    // Seats of the stay's rooms by trimmed room name (largest when a name repeats).
    public static async Task<Dictionary<string, int>> RoomCapsAsync(ApplicationDbContext db, Guid stayId)
    {
        var rooms = await db.StayRooms.AsNoTracking().Where(r => r.StayListingId == stayId)
            .Select(r => new { r.RoomName, r.MaxGuests }).ToListAsync();
        return rooms.GroupBy(r => r.RoomName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(r => (int)r.MaxGuests), StringComparer.OrdinalIgnoreCase);
    }

    // ---------- admin take-down (B7) ----------

    // Open rows the host never confirmed close; a confirmed or booked row stays and is flagged instead.
    public static bool ClosedByTakeDown(string status) => TripEnquiryStatuses.IsUnconfirmed(status) || status == TripEnquiryStatuses.FareSent;

    // Inside the take-down transaction: shortlist rows close, unconfirmed rows become NotAvailable (ListingRemoved), their offers
    // are superseded, booked rows keep their status and get a chat line plus a traveller push. Pushes go out after commit.
    public static async Task<IReadOnlyList<TripClosurePush>> CloseOnTakeDownAsync(
        ApplicationDbContext db, ITripChatService chat, ITripEnquiryRepository enquiries, string kind, Guid listingId, List<TripChatPost> posts)
    {
        var shortlistKinds = kind == ListingKinds.Stay ? new[] { TripShortlistKinds.Stay } : new[] { TripShortlistKinds.Taxi, TripShortlistKinds.Car };
        var now = DateTime.UtcNow;
        await db.TripShortlistItems
            .Where(i => i.State == TripShortlistStates.Active && i.ListingId == listingId && shortlistKinds.Contains(i.Kind))
            .ExecuteUpdateAsync(set => set
                .SetProperty(i => i.State, TripShortlistStates.Closed)
                .SetProperty(i => i.ClosedReason, TripShortlistClosedReasons.Unavailable)
                .SetProperty(i => i.ClosedAt, now));

        var rows = await db.TripEnquiries
            .Where(e => e.Kind == kind && e.ListingId == listingId && TripEnquiryStatuses.Open.Contains(e.Status))
            .ToListAsync();
        var pushes = new List<TripClosurePush>();
        foreach (var e in rows)
        {
            var closes = ClosedByTakeDown(e.Status);
            if (closes)
            {
                e.Status = TripEnquiryStatuses.NotAvailable;
                e.FailReason = TripFailReasons.ListingRemoved;
                e.UpdatedAt = now;
                await TripEnquiryOps.CloseOpenOfferAsync(db, chat, e, TripOfferStatuses.Superseded, TripCardStatuses.Superseded, posts, now);
                await JourneyLegSync.SyncAsync(db, e, now);
            }
            await TripEnquiryOps.PostSystemAsync(db, chat, e, e.OwnerUserId, TripSystemLines.ListingRemoved, posts);
            if (e.TravellerUserId is not { } travellerId) continue;
            var ctx = await enquiries.LoadPushContextAsync(e, travellerId);
            pushes.Add(new TripClosurePush(TripNotifications.TripListingRemoved(e, ctx), $"taken:{e.Id}", e.TripId, e.Id));
        }
        await db.SaveChangesAsync();
        return pushes;
    }

    // After commit; never throws.
    public static async Task AfterTakeDownAsync(ITripChatService chat, ITripNotifier notifier, ILogger logger, List<TripChatPost> posts, IReadOnlyList<TripClosurePush> pushes)
    {
        try { await chat.AfterCommitAsync(posts); }
        catch (Exception ex) { logger.LogError(ex, "trip.takedown.broadcast_failed"); }
        foreach (var p in pushes)
        {
            try { await notifier.SendOnceAsync(p.Notification, p.DedupeKey, p.TripId, p.EnquiryId); }
            catch (Exception ex) { logger.LogError(ex, "trip.takedown.push_failed {EnquiryId}", p.EnquiryId); }
        }
    }
}

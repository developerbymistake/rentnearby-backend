using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Silence rules. Nothing here cancels a booking: a reminder only notifies, a paused driver closes his unanswered local leads.
public static class TripSilenceRules
{
    public const int BookedSilenceHours = 24, WindowHours = 48;

    // FailReason on a NotAvailable lead whose driver went off; the client shows "Driver is off right now".
    public const string DriverPaused = "DriverPaused";

    public static string BookedKey(Guid enquiryId) => $"bookedsilence:{enquiryId}";

    public static DateTime BookedDueUtc(DateTime bookedAtUtc) => bookedAtUtc.AddHours(BookedSilenceHours);

    // "I booked" waiting for the driver: one reminder once the answer is 24 h late, attempted for 48 h only.
    public static bool BookedDue(TripEnquiry e, DateTime nowUtc)
        => e.Status == TripEnquiryStatuses.BookedByTraveller
           && e.BookedByTravellerAt is { } at
           && nowUtc >= BookedDueUtc(at)
           && nowUtc < BookedDueUtc(at).AddHours(WindowHours);

    // Unanswered local sightseeing leads only; a booked row, a journey car and a trip-less lead are left alone.
    public static bool ClosesWhenPaused(TripEnquiry e)
        => TripSegmentRows.LocalTaxi.Compile()(e)
           && (e.Status == TripEnquiryStatuses.Asked || e.Status == TripEnquiryStatuses.FareSent);

    public static NotificationEvent BookedReminder(TripEnquiry e, TripPushContext ctx)
    {
        var n = TripNotifications.TripNoReply(e, ctx);
        n.Body = "You marked it booked. Call the driver to confirm.";
        return n;
    }

    public static NotificationEvent DriverOff(TripEnquiry e, TripPushContext ctx)
    {
        var n = TripNotifications.TripNotAvailable(e, ctx);
        n.Title = "Driver is off right now";
        n.Body = "Ask another driver.";
        return n;
    }
}

public static class TripLeadSilence
{
    // Timer pass: BookedByTraveller rows 24 h without the driver's answer get one push (SendOnce key bookedsilence:{id}).
    public static async Task<int> RemindBookedAsync(
        ApplicationDbContext db, ITripEnquiryRepository enquiries, ITripNotifier notifier, DateTime nowUtc, int take,
        ILogger logger, CancellationToken ct)
    {
        var due = nowUtc.AddHours(-TripSilenceRules.BookedSilenceHours);
        var oldest = due.AddHours(-TripSilenceRules.WindowHours);
        var booked = TripEnquiryStatuses.BookedByTraveller;
        var rows = await db.TripEnquiries.AsNoTracking()
            .Where(e => e.Status == booked && e.BookedByTravellerAt <= due && e.BookedByTravellerAt > oldest
                && e.TripId != null && e.TravellerUserId != null
                && db.Trips.Any(t => t.Id == e.TripId && t.MutedAt == null && t.Status != TripStatuses.Completed)
                && !db.TripPushLogs.Any(p => p.DedupeKey == "bookedsilence:" + e.Id))
            .OrderBy(e => e.Id)
            .Take(Math.Max(1, take))
            .ToListAsync(ct);

        var done = 0;
        foreach (var e in rows)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var ctx = await enquiries.LoadPushContextAsync(e, e.TravellerUserId!.Value);
                if (await notifier.SendOnceAsync(TripSilenceRules.BookedReminder(e, ctx), TripSilenceRules.BookedKey(e.Id), e.TripId, e.Id))
                    done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Trip silence: booked reminder failed for {EnquiryId}", e.Id);
            }
        }
        return done;
    }

    // The driver paused: his unanswered local leads become NotAvailable / DriverPaused and the traveller is told.
    // Runs inside the caller's transaction; the caller sends the pushes after commit (ITripEnquiryClosure.AfterCommitAsync).
    public static async Task<IReadOnlyList<TripClosurePush>> ClosePausedDriverLeadsAsync(
        ApplicationDbContext db, ITripEnquiryRepository enquiries, Guid taxiId, DateTime nowUtc)
    {
        var rows = await db.TripEnquiries
            .Where(TripSegmentRows.LocalTaxi)
            .Where(e => e.ListingId == taxiId && (e.Status == TripEnquiryStatuses.Asked || e.Status == TripEnquiryStatuses.FareSent))
            .ToListAsync();
        if (rows.Count == 0) return [];

        var pushes = new List<TripClosurePush>();
        foreach (var e in rows)
        {
            e.Status = TripEnquiryStatuses.NotAvailable;
            e.FailReason = TripSilenceRules.DriverPaused;
            e.UpdatedAt = nowUtc;
            await JourneyLegSync.SyncAsync(db, e, nowUtc);
            if (e.TravellerUserId is not { } travellerId) continue;
            var ctx = await enquiries.LoadPushContextAsync(e, travellerId);
            pushes.Add(new TripClosurePush(TripSilenceRules.DriverOff(e, ctx), $"gone-paused:{e.Id}", e.TripId, e.Id));
        }
        await db.SaveChangesAsync();
        return pushes;
    }
}

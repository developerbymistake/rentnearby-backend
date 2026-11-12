using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Every 5 min (first pass at startup), one scope per pass, one transaction per row:
//   1. Pending offers past ValidUntil -> Expired; the enquiry goes back to Asked (or Expired when its own window is over); push to both sides (offerexp:{offerId}:t|o).
//   2. Unconfirmed enquiries past ExpiresAt or whose check-in day has passed -> Expired, silently (the one final push is the morning reminder run);
//      the shortlist row of that listing closes as NoReply so it joins the "No reply" list.
//   3. IsUrgent refresh for open rows whose check-in is now within 72 h.
//   4. Purges: push logs after 90 days, trip-less enquiries after 180 days, published outbox rows after 3 days,
//      closed shortlist rows after 30 days, reminder runs after 30 days.
//   5. Advance holds past HoldUntil -> Failed/HoldExpired; cards rewritten; push to both sides (hold:{id}:t|o).
//   6. Host reminder while the proof is sent and the hold runs, on HostReminderSchedule slots (proof:{id}:{proofSentAt}:{slot});
//      the host keeps one inbox row per enquiry.
//   7. Taxi FareSent leads past ExpiresAt -> Expired, silently.
//   8. Shortlist items past ClosesAt (72 h) -> Closed/NoReply, silently; the morning run sends the one final push.
//   9. Trip date reminders: 3 days before (d3, 48 h window), start day (d0, until that day ends), day after the last day
//      (done, until that day ends); keyed by date, muted trips skipped.
//  10. Planned trips 7 days past the last day with nothing open -> Completed (no push, runs even when muted).
//  11. Travel-eve: 18:00 IST the evening before a travel day between two cities, once (trv:{tripId}:{date}); muted trips skipped.
// Passes 6, 9, 10 and 11 read keyset pages of only un-done work (key not logged / nothing open) until exhausted.
// Order: 1, 2, 7, 5, 6, 8, 9, 11, 10, 3, 4. Skipped while TripExecution:Enabled is false.
public class TripTimerService(
    IServiceScopeFactory scopeFactory, IOptionsMonitor<TripExecutionOptions> options, ILogger<TripTimerService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private const int Batch = 200, ShortlistCloseBatch = 2000;
    private bool _loggedDisabled;
    private DateOnly _lastDailyStamp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled)
        {
            if (!_loggedDisabled) logger.LogInformation("Trip timer: trip execution is disabled, passes skipped");
            _loggedDisabled = true;
            return;
        }
        _loggedDisabled = false;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var offers = await OfferExpiryPassAsync(sp, ct);
            var enquiries = await EnquiryExpiryPassAsync(sp, ct);
            var leads = await FareSentExpiryPassAsync(sp, ct);
            var hold = await HoldExpiryPassAsync(sp, ct);
            var reminders = await HostReminderPassAsync(sp, ct);
            var closed = await ShortlistClosePassAsync(sp, ct);
            var bookedSilence = await BookedSilencePassAsync(sp, ct);
            var dates = await DateReminderPassAsync(sp, ct);
            var travelEve = await TravelEvePassAsync(sp, ct);
            var completed = await AutoCompletePassAsync(sp, ct);
            var urgent = await UrgentPassAsync(sp);
            var purged = 0;
            var today = TripExecutionRules.TodayIst(DateTime.UtcNow);
            if (_lastDailyStamp != today)
            {
                purged += await DailyFinalStampPassAsync(sp, DateTime.UtcNow);
                _lastDailyStamp = today;
            }
            if (offers + enquiries + leads + hold + reminders + closed + bookedSilence + dates + travelEve + completed + urgent + purged > 0)
                logger.LogInformation(
                    "Trip timer: offers={Offers} enquiries={Enquiries} urgent={Urgent} purged={Purged} hold={Hold} reminders={Reminders} leads={Leads} closed={Closed} bookedsilence={BookedSilence} dates={Dates} traveleve={TravelEve} completed={Completed}",
                    offers, enquiries, urgent, purged, hold, reminders, leads, closed, bookedSilence, dates, travelEve, completed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Trip timer pass failed");
        }
    }

    private async Task<int> OfferExpiryPassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var chat = sp.GetRequiredService<ITripChatService>();
        var notifier = sp.GetRequiredService<ITripNotifier>();
        var now = DateTime.UtcNow;
        var due = await unitOfWork.TripOffers.GetExpiredPendingAsync(now, Batch);
        var done = 0;

        foreach (var row in due)
        {
            if (ct.IsCancellationRequested) break;
            var posts = new List<TripChatPost>();
            TripEnquiry? lapsed = null;
            await unitOfWork.BeginTransactionAsync();
            try
            {
                var offer = await db.TripOffers.FirstOrDefaultAsync(o => o.Id == row.Id, ct);
                if (offer == null || offer.Status != TripOfferStatuses.Pending || offer.ValidUntil > now)
                {
                    await unitOfWork.RollbackTransactionAsync();
                    continue;
                }
                offer.Status = TripOfferStatuses.Expired;
                offer.RespondedAt = now;
                if (offer.MessageId is { } messageId && await chat.RewriteStatusAsync(messageId, TripCardStatuses.Expired) is { } post)
                    posts.Add(post);
                await db.SaveChangesAsync(ct);

                var enquiry = await db.TripEnquiries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == offer.TripEnquiryId, ct);
                if (enquiry != null && enquiry.Status == TripEnquiryStatuses.Offered)
                {
                    var next = enquiry.ExpiresAt > now ? TripEnquiryStatuses.Asked : TripEnquiryStatuses.Expired;
                    var moved = await unitOfWork.TripEnquiries.TryTransitionAsync(enquiry.Id, [TripEnquiryStatuses.Offered], next, null, now);
                    await TripEnquiryOps.PostSystemAsync(db, chat, enquiry, offer.SenderUserId, TripSystemLines.OfferExpired, posts);
                    if (moved > 0)
                    {
                        enquiry.Status = next;
                        enquiry.UpdatedAt = now;
                        lapsed = enquiry;
                    }
                }

                await unitOfWork.CommitTransactionAsync();
                done++;
            }
            catch (DbUpdateConcurrencyException)
            {
                await unitOfWork.RollbackTransactionAsync();
                posts.Clear();
                lapsed = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await unitOfWork.RollbackTransactionAsync();
                posts.Clear();
                lapsed = null;
                logger.LogError(ex, "Trip timer: offer expiry failed for {OfferId}", row.Id);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
            await chat.AfterCommitAsync(posts);
            if (lapsed != null)
                await TripPushSender.SendAsync(
                [
                    new TripPendingPush(lapsed, lapsed.TravellerUserId, TripNotifications.TripOfferExpired,
                        DedupeKey: $"offerexp:{row.Id}:t"),
                    new TripPendingPush(lapsed, lapsed.OwnerUserId, TripNotifications.TripOfferLapsed,
                        DedupeKey: $"offerexp:{row.Id}:o"),
                ], unitOfWork.TripEnquiries, notifier, logger);
        }
        return done;
    }

    private async Task<int> EnquiryExpiryPassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var chat = sp.GetRequiredService<ITripChatService>();
        var now = DateTime.UtcNow;
        var today = TripExecutionRules.TodayIst(now);
        var due = await unitOfWork.TripEnquiries.GetExpiredUnconfirmedAsync(now, today, Batch);
        var done = 0;

        foreach (var row in due)
        {
            if (ct.IsCancellationRequested) break;
            var posts = new List<TripChatPost>();
            await unitOfWork.BeginTransactionAsync();
            try
            {
                var rows = await unitOfWork.TripEnquiries.TryTransitionAsync(
                    row.Id, TripEnquiryStatuses.Unconfirmed, TripEnquiryStatuses.Expired, null, now);
                if (rows == 0)
                {
                    await unitOfWork.RollbackTransactionAsync();
                    continue;
                }
                await TripEnquiryOps.CloseOpenOfferAsync(db, chat, row, TripOfferStatuses.Expired, TripCardStatuses.Expired, posts, now);
                await TripEnquiryOps.PostSystemAsync(db, chat, row, row.OwnerUserId, TripSystemLines.Expired, posts);
                await db.SaveChangesAsync(ct);
                row.Status = TripEnquiryStatuses.Expired;
                row.UpdatedAt = now;
                await JourneyLegSync.SyncAsync(db, row, now);
                await CloseShortlistOfAsync(db, row, now, ct);
                await unitOfWork.CommitTransactionAsync();
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await unitOfWork.RollbackTransactionAsync();
                posts.Clear();
                logger.LogError(ex, "Trip timer: enquiry expiry failed for {EnquiryId}", row.Id);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }

            await chat.AfterCommitAsync(posts);
        }
        return done;
    }

    private async Task<int> FareSentExpiryPassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var now = DateTime.UtcNow;
        var due = await unitOfWork.TripEnquiries.GetExpiredFareSentAsync(now, Batch);
        var done = 0;

        foreach (var row in due)
        {
            if (ct.IsCancellationRequested) break;
            await unitOfWork.BeginTransactionAsync();
            try
            {
                var rows = await unitOfWork.TripEnquiries.TryTransitionAsync(
                    row.Id, [TripEnquiryStatuses.FareSent], TripEnquiryStatuses.Expired, null, now);
                if (rows == 0)
                {
                    await unitOfWork.RollbackTransactionAsync();
                    continue;
                }
                row.Status = TripEnquiryStatuses.Expired;
                row.UpdatedAt = now;
                await JourneyLegSync.SyncAsync(db, row, now);
                await unitOfWork.CommitTransactionAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await unitOfWork.RollbackTransactionAsync();
                logger.LogError(ex, "Trip timer: lead expiry failed for {EnquiryId}", row.Id);
                continue;
            }

            done++;
        }
        return done;
    }

    private async Task<int> HoldExpiryPassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var chat = sp.GetRequiredService<ITripChatService>();
        var notifier = sp.GetRequiredService<ITripNotifier>();
        var now = DateTime.UtcNow;
        var due = await unitOfWork.TripEnquiries.GetDueHoldsAsync(now, Batch);
        var done = 0;

        foreach (var row in due)
        {
            if (ct.IsCancellationRequested) break;
            var posts = new List<TripChatPost>();
            TripEnquiry? expired = null;
            await unitOfWork.BeginTransactionAsync();
            try
            {
                var rows = await unitOfWork.TripEnquiries.TryExpireHoldAsync(row.Id, now);
                if (rows == 0)
                {
                    await unitOfWork.RollbackTransactionAsync();
                    continue;
                }
                if (row.ConversationId is { } conversationId)
                {
                    await RewriteCardAsync(chat, conversationId, TripMessageTypes.AdvanceRequest, row.Id, TripCardStatuses.HoldExpired, posts);
                    if (row.ProofSentAt != null)
                        await RewriteCardAsync(chat, conversationId, TripMessageTypes.AdvanceProofSent, row.Id, TripCardStatuses.HoldExpired, posts);
                }
                await TripEnquiryOps.PostSystemAsync(db, chat, row, row.OwnerUserId, TripSystemLines.HoldExpired, posts);
                await db.SaveChangesAsync(ct);
                await unitOfWork.CommitTransactionAsync();
                row.Status = TripEnquiryStatuses.Failed;
                row.FailReason = TripFailReasons.HoldExpired;
                row.AdvanceStatus = TripAdvanceStatuses.HoldExpired;
                row.UpdatedAt = now;
                expired = row;
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await unitOfWork.RollbackTransactionAsync();
                posts.Clear();
                logger.LogError(ex, "Trip timer: hold expiry failed for {EnquiryId}", row.Id);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }

            await chat.AfterCommitAsync(posts);
            if (expired != null)
                await TripPushSender.SendAsync(
                [
                    new TripPendingPush(expired, expired.TravellerUserId, TripNotifications.TripHoldExpired,
                        DedupeKey: TripAdvanceRules.HoldKey(expired.Id, "t")),
                    new TripPendingPush(expired, expired.OwnerUserId, TripNotifications.TripHoldExpired,
                        DedupeKey: TripAdvanceRules.HoldKey(expired.Id, "o")),
                ], unitOfWork.TripEnquiries, notifier, logger);
        }
        return done;
    }

    // The due query is the state check: a host mark between the query and the send costs at most one extra push.
    // Slots come from HostReminderSchedule (absolute, quiet hours, capped); the host keeps one inbox row per enquiry.
    private async Task<int> HostReminderPassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var notifier = sp.GetRequiredService<ITripNotifier>();
        var now = DateTime.UtcNow;
        var done = 0;
        Guid? after = null;
        do
        {
            if (ct.IsCancellationRequested) break;
            var (due, next) = await unitOfWork.TripEnquiries.GetDueReminderAsync(now, after, Batch);
            after = next;
            foreach (var row in due)
            {
                if (ct.IsCancellationRequested) break;
                if (row.OwnerUserId is not { } ownerId || row.ProofSentAt is not { } proofSentAt || row.HoldUntil is not { } hold) continue;
                var count = (int)row.HostReminderCount;
                if (HostReminderSchedule.Due(proofSentAt, hold, count, now) is not { } slot) continue;
                try
                {
                    var ctx = await unitOfWork.TripEnquiries.LoadPushContextAsync(row, ownerId);
                    var n = TripNotifications.TripAdvanceProofSent(row, ctx);
                    var outcome = await notifier.TrySendOnceAsync(n, TripAdvanceRules.ProofKey(row.Id, proofSentAt, slot.Number), row.TripId, row.Id);
                    // Failed = nothing logged: keep the count so the next tick retries this slot. Duplicate = an earlier pass sent it.
                    if (!TripSendOutcomes.AdvancesCursor(outcome)) continue;
                    await unitOfWork.TripEnquiries.TryBumpReminderAsync(row.Id, count, slot.NextCount, now);
                    if (outcome != TripSendOutcome.Sent) continue;
                    done++;
                    await unitOfWork.Notifications.DeleteSupersededAsync(ownerId, n.Type, row.Id, n.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Trip timer: host reminder failed for {EnquiryId}", row.Id);
                }
            }
        }
        while (after != null);
        return done;
    }

    // Exact and silent: items whose 72 h clock ended become Closed/NoReply (status only; the morning run sends the one final push).
    // Batched by id with SKIP LOCKED, so any number of instances can run it.
    private async Task<int> ShortlistClosePassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;
        var done = 0;
        // A step that already has a confirmed or booked enquiry is settled, not unanswered: it ends as Booked, never NoReply.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "TripShortlistItems" s SET "State" = 'Booked', "ClosedReason" = 'Booked', "ClosedAt" = {now}
            WHERE s."State" IN ('Active', 'Sent') AND s."ClosesAt" <= {now}
              AND EXISTS (SELECT 1 FROM "TripEnquiries" c WHERE c."TripId" = s."TripId" AND c."StepKey" = s."StepKey"
                          AND c."Status" IN ('ConfirmedByBoth', 'BookedByTraveller'))
            """, ct);
        while (!ct.IsCancellationRequested)
        {
            var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "TripShortlistItems" SET "State" = 'Closed', "ClosedReason" = 'NoReply', "ClosedAt" = {now}
                WHERE "Id" IN (SELECT "Id" FROM "TripShortlistItems" WHERE "State" = 'Active' AND "ClosesAt" <= {now}
                               ORDER BY "ClosesAt" LIMIT {ShortlistCloseBatch} FOR UPDATE SKIP LOCKED)
                """, ct);
            done += rows;
            if (rows < ShortlistCloseBatch) break;
        }
        return done;
    }

    // The enquiry that expired closes its shortlist row too (State Sent -> Closed/NoReply), so it leaves the active shortlist.
    private static Task<int> CloseShortlistOfAsync(ApplicationDbContext db, TripEnquiry e, DateTime now, CancellationToken ct)
        => e.TripId is { } tripId && e.StepKey is { } step
            ? db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "TripShortlistItems" SET "State" = 'Closed', "ClosedReason" = 'NoReply', "ClosedAt" = {now}
                WHERE "TripId" = {tripId} AND "StepKey" = {step} AND "ListingId" = {e.ListingId} AND "State" IN ('Active', 'Sent')
                  AND NOT EXISTS (SELECT 1 FROM "TripEnquiries" c WHERE c."TripId" = {tripId} AND c."StepKey" = {step}
                                  AND c."Status" IN ('ConfirmedByBoth', 'BookedByTraveller'))
                """, ct)
            : Task.FromResult(0);

    private async Task<int> BookedSilencePassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var notifier = sp.GetRequiredService<ITripNotifier>();
        return await TripLeadSilence.RemindBookedAsync(db, unitOfWork.TripEnquiries, notifier, DateTime.UtcNow, Batch, logger, ct);
    }

    private async Task<int> DateReminderPassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var journeyRepo = sp.GetRequiredService<ISavedJourneyRepository>();
        var notifier = sp.GetRequiredService<ITripNotifier>();
        var now = DateTime.UtcNow;
        var today = TripExecutionRules.TodayIst(now);
        var startsIn = TripReminderRules.StartsInOpenDates(now);
        var startsToday = TripReminderRules.TodayOpenDates(now);
        var markComplete = TripReminderRules.MarkCompleteOpenDates(now);
        var done = 0;
        Guid? after = null;
        do
        {
            if (ct.IsCancellationRequested) break;
            var (trips, next) = await unitOfWork.Trips.GetDateReminderCandidatesAsync(startsIn, startsToday, markComplete, after, Batch);
            after = next;
            done += await DateReminderPageAsync(trips, today, now, db, unitOfWork, journeyRepo, notifier, ct);
        }
        while (after != null);
        return done;
    }

    private async Task<int> DateReminderPageAsync(
        IReadOnlyList<Trip> trips, DateOnly today, DateTime now, ApplicationDbContext db, IUnitOfWork unitOfWork,
        ISavedJourneyRepository journeyRepo, ITripNotifier notifier, CancellationToken ct)
    {
        var done = 0;
        foreach (var trip in trips)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var plan = ReadPlan(trip);
                var enquiries = await unitOfWork.TripEnquiries.GetByTripAsync(trip.Id);
                var linked = await journeyRepo.GetByTripAsync(trip.Id, trip.OwnerUserId);

                if (trip.StartDate is { } start)
                {
                    if (TripReminderRules.StartsInOpen(start, now)
                        && TripReminderRules.ShouldSendStartsIn(trip, plan, enquiries, today, linked)
                        && await TripRemindableAsync(db, trip.Id, ct)
                        && await notifier.SendOnceAsync(
                            TripReminderNotifications.StartsIn(
                                trip.OwnerUserId, trip.Id,
                                plan.Legs is { Count: > 1 } ? TripReminderRules.RouteTitle(plan.LegCities) : trip.CityName,
                                TripReminderRules.DaysLeft(start, today),
                                TripReminderRules.StartsInBody(trip, plan, enquiries, today, linked)),
                            TripReminderRules.StartsInKey(trip.Id, start), trip.Id, null))
                        done++;

                    if (TripReminderRules.TodayOpen(start, now)
                        && await TripRemindableAsync(db, trip.Id, ct)
                        && await notifier.SendOnceAsync(
                            TripReminderNotifications.StartsToday(trip.OwnerUserId, trip.Id, TripReminderRules.TodayBody(enquiries, start)),
                            TripReminderRules.TodayKey(trip.Id, start), trip.Id, null))
                        done++;
                }

                if (trip.EndDate is { } end
                    && TripReminderRules.MarkCompleteOpen(end, now)
                    && await TripRemindableAsync(db, trip.Id, ct)
                    && await notifier.SendOnceAsync(
                        TripReminderNotifications.MarkComplete(trip.OwnerUserId, trip.Id, trip.CityName),
                        TripReminderRules.MarkCompleteKey(trip.Id, end), trip.Id, null))
                    done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Trip timer: date reminder failed for {TripId}", trip.Id);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
        return done;
    }

    // At most one travel day per date per trip (4 legs max), so at most one push per trip per tick.
    private async Task<int> TravelEvePassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var journeyRepo = sp.GetRequiredService<ISavedJourneyRepository>();
        var notifier = sp.GetRequiredService<ITripNotifier>();
        var now = DateTime.UtcNow;
        var tomorrow = TripExecutionRules.TodayIst(now).AddDays(1);
        if (!TripReminderRules.TravelEveInWindow(tomorrow, now)) return 0;
        var done = 0;
        Guid? after = null;
        do
        {
            if (ct.IsCancellationRequested) break;
            var (trips, next) = await unitOfWork.Trips.GetTravelEveCandidatesAsync(tomorrow, after, Batch);
            after = next;
            done += await TravelEvePageAsync(trips, tomorrow, db, unitOfWork, journeyRepo, notifier, ct);
        }
        while (after != null);
        return done;
    }

    private async Task<int> TravelEvePageAsync(
        IReadOnlyList<Trip> trips, DateOnly tomorrow, ApplicationDbContext db, IUnitOfWork unitOfWork,
        ISavedJourneyRepository journeyRepo, ITripNotifier notifier, CancellationToken ct)
    {
        var done = 0;
        foreach (var trip in trips)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (trip.StartDate is not { } start) continue;
                var plan = ReadPlan(trip);
                if (plan.Legs is not { Count: > 1 } legs) continue;
                var day = tomorrow.DayNumber - start.DayNumber + 1;
                if (!plan.IsTravelDay(day)) continue;
                var k = plan.LegIndexOf(day);
                if (!await TripRemindableAsync(db, trip.Id, ct)) continue;

                var enquiries = await unitOfWork.TripEnquiries.GetByTripAsync(trip.Id);
                var arrival = legs[k - 1]?.Arrival;
                SavedJourney? journey = null;
                IReadOnlyDictionary<Guid, JourneyTaxiInfo> taxis = new Dictionary<Guid, JourneyTaxiInfo>();
                if (TripTravelRules.JourneyIdOf(arrival) is { } journeyId)
                {
                    var linked = await journeyRepo.GetByTripAsync(trip.Id, trip.OwnerUserId);
                    journey = linked.FirstOrDefault(j => j.Id == journeyId);
                    if (journey != null)
                        taxis = await journeyRepo.LoadTaxisAsync(journey.Legs.Select(l => l.DriverTaxiId).ToList());
                }

                var (title, body) = TripTravelEveReminder.Build(plan, k, tomorrow, enquiries, journey, taxis);
                if (await notifier.SendOnceAsync(
                        TripReminderNotifications.TravelEve(trip.OwnerUserId, trip.Id, title, body),
                        TripReminderRules.TravelEveKey(trip.Id, tomorrow), trip.Id, null))
                    done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Trip timer: travel-eve reminder failed for {TripId}", trip.Id);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
        return done;
    }

    private async Task<int> AutoCompletePassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var journeyRepo = sp.GetRequiredService<ISavedJourneyRepository>();
        var now = DateTime.UtcNow;
        var endBefore = TripExecutionRules.TodayIst(now).AddDays(-TripReminderRules.AutoCompleteDays);
        var done = 0;
        Guid? after = null;
        do
        {
            if (ct.IsCancellationRequested) break;
            var (trips, next) = await unitOfWork.Trips.GetAutoCompleteCandidatesAsync(endBefore, after, Batch);
            after = next;
            foreach (var trip in trips)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var enquiries = await unitOfWork.TripEnquiries.GetByTripAsync(trip.Id);
                    if (enquiries.Any(TripBookingStates.IsOpenForCompletion)) continue;
                    var linked = await journeyRepo.GetByTripAsync(trip.Id, trip.OwnerUserId);
                    // A trip nobody worked on is left for the traveller (no enquiry, no mark, no journey ask).
                    if (TripCompleteRules.IsUntouched(TripEasyRows.Build(trip, ReadPlan(trip), enquiries, linked))) continue;
                    if (await unitOfWork.Trips.TryCompleteAsync(trip.Id, now) == 0) continue;
                    logger.LogInformation("trip.completed {TripId} auto", trip.Id);
                    done++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Trip timer: auto-complete failed for {TripId}", trip.Id);
                }
            }
        }
        while (after != null);
        return done;
    }

    // Mute silences traveller reminders only; a Completed trip gets none.
    private static async Task<bool> TripRemindableAsync(ApplicationDbContext db, Guid tripId, CancellationToken ct)
        => await db.Trips.AsNoTracking().AnyAsync(
            t => t.Id == tripId && t.MutedAt == null && t.Status != TripStatuses.Completed, ct);

    private static TripPlan ReadPlan(Trip trip) => TripPlanReader.Read(trip);

    private static async Task RewriteCardAsync(
        ITripChatService chat, Guid conversationId, string type, Guid enquiryId, string status, List<TripChatPost> posts)
    {
        if (await chat.FindCardMessageIdAsync(conversationId, type, enquiryId) is not { } messageId) return;
        if (await chat.RewriteStatusAsync(messageId, status) is { } post) posts.Add(post);
    }

    private static async Task<int> UrgentPassAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;
        // IsUrgent(d) <=> 00:00 IST of d is less than 72 h away.
        var limit = now.AddHours(TripExecutionRules.UrgentHours);
        var cutoff = TripExecutionRules.TodayIst(limit);
        if (TripExecutionRules.IstMidnightUtc(cutoff) >= limit) cutoff = cutoff.AddDays(-1);
        return await db.TripEnquiries
            .Where(e => !e.IsUrgent && e.CheckIn != null && e.CheckIn <= cutoff && TripEnquiryStatuses.Open.Contains(e.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsUrgent, true).SetProperty(e => e.UpdatedAt, now));
    }

    // Once per IST day: rows that ended more than FinalWindowDays ago and were never announced are stamped, so the morning run
    // (which only looks at the last FinalWindowDays) and its indexes never carry them. Stamping is not a push.
    private static async Task<int> DailyFinalStampPassAsync(IServiceProvider sp, DateTime now)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var cutoff = now.AddDays(-TripReminderRules.FinalWindowDays);
        var shortlist = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "TripShortlistItems" SET "FinalPushedAt" = {now} WHERE "Id" IN (
              SELECT "Id" FROM "TripShortlistItems"
              WHERE "State" = 'Closed' AND "ClosedReason" = 'NoReply' AND "FinalPushedAt" IS NULL AND "ClosedAt" < {cutoff} LIMIT 20000)
            """);
        var enquiries = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "TripEnquiries" SET "FinalPushedAt" = {now} WHERE "Id" IN (
              SELECT "Id" FROM "TripEnquiries"
              WHERE "Status" = 'Expired' AND "FinalPushedAt" IS NULL AND "UpdatedAt" < {cutoff} LIMIT 20000)
            """);
        return shortlist + enquiries;
    }
}

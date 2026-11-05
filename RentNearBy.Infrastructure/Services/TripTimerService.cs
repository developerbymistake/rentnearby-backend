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
//   2. Unconfirmed enquiries past ExpiresAt or whose check-in day has passed -> Expired; push once (exp:{id}).
//   3. IsUrgent refresh for open rows whose check-in is now within 72 h.
//   4. Purges: push logs after 90 days, trip-less enquiries after 180 days.
//   5. Advance holds past HoldUntil -> Failed/HoldExpired; cards rewritten; push to both sides (hold:{id}:t|o).
//   6. Hourly host reminder while the proof is sent and the hold runs (proof:{id}:{proofSentAt}:{hour}).
//   7. Taxi FareSent leads past ExpiresAt -> Expired; push once (exp:{id}).
//   8. Asked enquiries 24 h old with no reply -> one traveller reminder (noreply:{id}; a journey car between cities:
//      one per travel segment, noreply:seg:{tripId}:{date}); muted trips skipped.
//   9. Host link unanswered for 24 h -> one traveller reminder (hostnc:{id}); muted trips skipped.
//  10. Trip date reminders: 3 days before (d3), start day (d0), day after the last day (done); keyed by date, muted trips skipped.
//  11. Planned trips 7 days past the last day with nothing open -> Completed (no push, runs even when muted).
//  12. Travel-eve: 18:00 IST the evening before a travel day between two cities, once (trv:{tripId}:{date}); muted trips skipped.
// Order: 1, 2, 7, 5, 6, 8, 9, 10, 12, 11, 3, 4. Skipped while TripExecution:Enabled is false.
public class TripTimerService(
    IServiceScopeFactory scopeFactory, IOptionsMonitor<TripExecutionOptions> options, ILogger<TripTimerService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private const int Batch = 200;
    private bool _loggedDisabled;

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
            var noReply = await NoReplyPassAsync(sp, ct);
            var hostNc = await HostNotConfirmedPassAsync(sp, ct);
            var dates = await DateReminderPassAsync(sp, ct);
            var travelEve = await TravelEvePassAsync(sp, ct);
            var completed = await AutoCompletePassAsync(sp, ct);
            var urgent = await UrgentPassAsync(sp);
            var purged = await PurgePassAsync(sp);
            if (offers + enquiries + leads + hold + reminders + noReply + hostNc + dates + travelEve + completed + urgent + purged > 0)
                logger.LogInformation(
                    "Trip timer: offers={Offers} enquiries={Enquiries} urgent={Urgent} purged={Purged} hold={Hold} reminders={Reminders} leads={Leads} noreply={NoReply} hostnc={HostNc} dates={Dates} traveleve={TravelEve} completed={Completed}",
                    offers, enquiries, urgent, purged, hold, reminders, leads, noReply, hostNc, dates, travelEve, completed);
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
        var notifier = sp.GetRequiredService<ITripNotifier>();
        var now = DateTime.UtcNow;
        var today = TripExecutionRules.TodayIst(now);
        var due = await unitOfWork.TripEnquiries.GetExpiredUnconfirmedAsync(now, today, Batch);
        var done = 0;

        foreach (var row in due)
        {
            if (ct.IsCancellationRequested) break;
            var posts = new List<TripChatPost>();
            TripEnquiry? expired = null;
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
                await unitOfWork.CommitTransactionAsync();
                expired = row;
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
            if (expired != null)
                await TripPushSender.SendAsync(
                    [new TripPendingPush(expired, expired.TravellerUserId, TripNotifications.TripEnquiryExpired, DedupeKey: $"exp:{expired.Id}")],
                    unitOfWork.TripEnquiries, notifier, logger);
        }
        return done;
    }

    private async Task<int> FareSentExpiryPassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var notifier = sp.GetRequiredService<ITripNotifier>();
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
            await TripPushSender.SendAsync(
                [new TripPendingPush(row, row.TravellerUserId, TripNotifications.TripDriverFareLapsed, DedupeKey: $"exp:{row.Id}")],
                unitOfWork.TripEnquiries, notifier, logger);
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
    private async Task<int> HostReminderPassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var notifier = sp.GetRequiredService<ITripNotifier>();
        var now = DateTime.UtcNow;
        var due = await unitOfWork.TripEnquiries.GetDueReminderAsync(now, Batch);
        var done = 0;

        foreach (var row in due)
        {
            if (ct.IsCancellationRequested) break;
            if (row.OwnerUserId is not { } ownerId || row.ProofSentAt is not { } proofSentAt) continue;
            var hourIndex = (int)row.HostReminderCount;
            try
            {
                var ctx = await unitOfWork.TripEnquiries.LoadPushContextAsync(row, ownerId);
                var n = TripNotifications.TripAdvanceProofSent(row, ctx);
                var sent = await notifier.SendOnceAsync(n, TripAdvanceRules.ProofKey(row.Id, proofSentAt, hourIndex), row.TripId, row.Id);
                // Not sent = this hour's key is already logged (an earlier pass sent it): move on to the next hour either way.
                await unitOfWork.TripEnquiries.TryBumpReminderAsync(row.Id, hourIndex, now);
                if (sent) done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Trip timer: host reminder failed for {EnquiryId}", row.Id);
            }
        }
        return done;
    }

    // Re-reads the row and its trip before sending; a case that moved on since the query is skipped.
    private async Task<int> NoReplyPassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var notifier = sp.GetRequiredService<ITripNotifier>();
        var now = DateTime.UtcNow;
        var due = await unitOfWork.TripEnquiries.GetNoReplyDueAsync(now, Batch);
        var done = 0;

        foreach (var row in due)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(row.Id);
                if (e == null || e.Status != TripEnquiryStatuses.Asked || e.ExpiresAt <= now
                    || e.TripId is not { } tripId || e.TravellerUserId is not { } travellerId
                    || !await TripRemindableAsync(db, tripId, ct))
                    continue;
                var ctx = await unitOfWork.TripEnquiries.LoadPushContextAsync(e, travellerId);
                var segment = TripSegmentRows.IsSegment(e) && e.CheckIn != null;
                var push = segment ? TripNotifications.TripNoReplySegment(e, ctx) : TripNotifications.TripNoReply(e, ctx);
                var key = segment ? TripReminderRules.NoReplySegmentKey(tripId, e.CheckIn!.Value) : TripReminderRules.NoReplyKey(e.Id);
                if (await notifier.SendOnceAsync(push, key, tripId, e.Id))
                    done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Trip timer: no-reply reminder failed for {EnquiryId}", row.Id);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
        return done;
    }

    private async Task<int> HostNotConfirmedPassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var notifier = sp.GetRequiredService<ITripNotifier>();
        var now = DateTime.UtcNow;
        var due = await unitOfWork.TripEnquiries.GetHostNotConfirmedDueAsync(now, Batch);
        var done = 0;

        foreach (var row in due)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(row.Id);
                if (e == null || e.Status != TripEnquiryStatuses.ConfirmedByTraveller || e.HostConfirmAnsweredAt != null
                    || e.TripId is not { } tripId || e.TravellerUserId is not { } travellerId
                    || !await TripRemindableAsync(db, tripId, ct))
                    continue;
                var ctx = await unitOfWork.TripEnquiries.LoadPushContextAsync(e, travellerId);
                if (await notifier.SendOnceAsync(
                        TripNotifications.TripHostNotConfirmed(e, ctx), TripReminderRules.HostNotConfirmedKey(e.Id), tripId, e.Id))
                    done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Trip timer: host-not-confirmed reminder failed for {EnquiryId}", row.Id);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
        return done;
    }

    private async Task<int> DateReminderPassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var notifier = sp.GetRequiredService<ITripNotifier>();
        var now = DateTime.UtcNow;
        var today = TripExecutionRules.TodayIst(now);
        var trips = await unitOfWork.Trips.GetReminderCandidatesAsync(
            today, today.AddDays(TripReminderRules.DaysBeforeStart), today.AddDays(-3), today, Batch);
        var done = 0;

        foreach (var trip in trips)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var plan = ReadPlan(trip);
                var enquiries = await unitOfWork.TripEnquiries.GetByTripAsync(trip.Id);

                if (trip.StartDate is { } start)
                {
                    if (TripReminderRules.InWindow(TripReminderRules.StartsInDueUtc(start), now)
                        && TripReminderRules.ShouldSendStartsIn(trip, plan, enquiries, today)
                        && await TripRemindableAsync(db, trip.Id, ct)
                        && await notifier.SendOnceAsync(
                            TripReminderNotifications.StartsIn(
                                trip.OwnerUserId, trip.Id,
                                plan.Legs is { Count: > 1 } ? TripReminderRules.RouteTitle(plan.LegCities) : trip.CityName,
                                TripReminderRules.DaysLeft(start, today),
                                TripReminderRules.StartsInBody(trip, plan, enquiries, today)),
                            TripReminderRules.StartsInKey(trip.Id, start), trip.Id, null))
                        done++;

                    if (TripReminderRules.InWindow(TripReminderRules.TodayDueUtc(start), now)
                        && await TripRemindableAsync(db, trip.Id, ct)
                        && await notifier.SendOnceAsync(
                            TripReminderNotifications.StartsToday(trip.OwnerUserId, trip.Id, TripReminderRules.TodayBody(enquiries, start)),
                            TripReminderRules.TodayKey(trip.Id, start), trip.Id, null))
                        done++;
                }

                if (trip.EndDate is { } end
                    && TripReminderRules.InWindow(TripReminderRules.MarkCompleteDueUtc(end), now)
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
                if (arrival?.Mode == TripTravelModes.Journey && arrival.JourneyId is { } journeyId)
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
        var now = DateTime.UtcNow;
        var endBefore = TripExecutionRules.TodayIst(now).AddDays(-TripReminderRules.AutoCompleteDays);
        var trips = await unitOfWork.Trips.GetAutoCompleteCandidatesAsync(endBefore, Batch);
        var done = 0;

        foreach (var trip in trips)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var enquiries = await unitOfWork.TripEnquiries.GetByTripAsync(trip.Id);
                if (enquiries.Any(TripBookingStates.IsOpenForCompletion)) continue;
                if (await unitOfWork.Trips.TryCompleteAsync(trip.Id, now) == 0) continue;
                logger.LogInformation("trip.completed {TripId} auto", trip.Id);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Trip timer: auto-complete failed for {TripId}", trip.Id);
            }
        }
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

    private static async Task<int> PurgePassAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var now = DateTime.UtcNow;
        var logs = await unitOfWork.TripPushLogs.PurgeOlderThanAsync(now.AddDays(-TripExecutionRules.PushLogRetentionDays));
        var orphanCutoff = now.AddDays(-TripExecutionRules.OrphanRetentionDays);
        var orphans = await db.TripEnquiries
            .Where(e => e.TripId == null && e.UpdatedAt < orphanCutoff)
            .ExecuteDeleteAsync();
        return logs + orphans;
    }
}

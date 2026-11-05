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

// Every 10 min (first pass after 1 min), journeys with TravelDate >= today (IST) and not Completed, only those with work
// (keyset pages to the end, GetWatchedPageAsync):
//   a chosen driver no longer public -> leg unavailable, journey DriverUnavailable, push once (jrn:p:...)
//   the same driver public again     -> leg available, status recomputed, push once per day (jrn:b:...)
//   18:00-24:00 IST the day before   -> push once per date (jrn:e:{id}:{date}), not when the trip's travel-eve push covers it (JourneyEveCover)
// A journey in a trip routes the paused/back pushes to the trip plan.
public class JourneyWatchService(
    IServiceScopeFactory scopeFactory, IOptionsMonitor<TripExecutionOptions> tripExecution, ILogger<JourneyWatchService> logger)
    : BackgroundService
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private const int Batch = 500;

    internal sealed record Pending(string Dedupe, NotificationEvent Event, Guid JourneyId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
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
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var repo = sp.GetRequiredService<ISavedJourneyRepository>();
            var notifier = sp.GetRequiredService<ITripNotifier>();

            var db = sp.GetRequiredService<ApplicationDbContext>();

            var now = DateTime.UtcNow;
            var today = JourneyRules.TodayIst(now);
            var tomorrow = today.AddDays(1);
            DateOnly? eveDate = JourneyEveRules.InWindow(tomorrow, now) ? tomorrow : null;

            int changed = 0, sent = 0;
            Guid? after = null;
            do
            {
                if (ct.IsCancellationRequested) break;
                var (journeys, next) = await repo.GetWatchedPageAsync(today, eveDate, after, Batch);
                after = next;
                var (c, s) = await PageAsync(journeys, repo, notifier, db, today, now, ct);
                changed += c;
                sent += s;
                db.ChangeTracker.Clear();
            }
            while (after != null);

            if (changed + sent > 0) logger.LogInformation("journey.watch changed={Changed} pushes={Pushes}", changed, sent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "journey.watch failed");
        }
    }

    private async Task<(int Changed, int Sent)> PageAsync(
        IReadOnlyList<SavedJourney> journeys, ISavedJourneyRepository repo, ITripNotifier notifier, ApplicationDbContext db,
        DateOnly today, DateTime now, CancellationToken ct)
    {
        if (journeys.Count == 0) return (0, 0);
        var taxiIds = journeys
            .SelectMany(j => j.Legs)
            .SelectMany(l => new[] { l.DriverTaxiId, l.Backup1TaxiId ?? Guid.Empty, l.Backup2TaxiId ?? Guid.Empty })
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var taxis = await repo.LoadTaxisAsync(taxiIds);
        var segmentIds = await SegmentJourneyIdsAsync(
            db, journeys.Where(j => j.TravelDate == today.AddDays(1)).ToList(), tripExecution.CurrentValue.Enabled, now, ct);
        var booked = await BookedLegsAsync(db, journeys, ct);

        int changed = 0, sent = 0;
        foreach (var journey in journeys)
        {
            if (ct.IsCancellationRequested) break;
            var pushes = new List<Pending>();
            var bookedLegs = booked.GetValueOrDefault(journey.Id);
            var dirty = Evaluate(journey, taxis, today, now, pushes, segmentIds, bookedLegs);
            if (dirty)
            {
                journey.Status = JourneyRules.JourneyStatusOf(journey.Legs, bookedLegs);
                journey.UpdatedAt = now;
                try
                {
                    await repo.SaveChangesAsync();
                    changed++;
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Only this journey is skipped: its stale entries leave the tracker, the next pass reads it fresh.
                    pushes.Clear();
                    foreach (var leg in journey.Legs) db.Entry(leg).State = EntityState.Detached;
                    db.Entry(journey).State = EntityState.Detached;
                    logger.LogInformation("journey.watch concurrent {JourneyId}", journey.Id);
                    continue;
                }
            }

            foreach (var p in pushes)
            {
                try
                {
                    if (await notifier.SendOnceAsync(p.Event, p.Dedupe, null, null)) sent++;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "journey.watch.push_failed {JourneyId}", p.JourneyId);
                }
            }
        }
        return (changed, sent);
    }

    // Journeys of tomorrow whose eve the trip's travel-eve push covers (one trips query and one push-log query per page).
    private static async Task<HashSet<Guid>> SegmentJourneyIdsAsync(
        ApplicationDbContext db, IReadOnlyList<SavedJourney> tomorrow, bool tripExecutionEnabled, DateTime now, CancellationToken ct)
    {
        var linked = tomorrow.Where(j => j.TripId != null).ToList();
        if (linked.Count == 0) return new HashSet<Guid>();
        var tripIds = linked.Select(j => j.TripId!.Value).Distinct().ToList();
        var trips = await db.Trips.AsNoTracking().Where(t => tripIds.Contains(t.Id)).ToListAsync(ct);
        var plans = trips.ToDictionary(t => t.Id, t => (Trip: t, Plan: TripPlanReader.Read(t)));
        var keys = linked.Select(j => TripReminderRules.TravelEveKey(j.TripId!.Value, j.TravelDate)).Distinct().ToList();
        var logged = (await db.TripPushLogs.AsNoTracking()
            .Where(p => keys.Contains(p.DedupeKey)).Select(p => p.DedupeKey).ToListAsync(ct)).ToHashSet();
        return linked
            .Where(j => plans.TryGetValue(j.TripId!.Value, out var p)
                && JourneyEveCover.Covered(
                    logged.Contains(TripReminderRules.TravelEveKey(j.TripId!.Value, j.TravelDate)),
                    tripExecutionEnabled, TripEveCovers(p.Trip, p.Plan, j), j.TravelDate, now))
            .Select(j => j.Id)
            .ToHashSet();
    }

    // Cars of trip journeys with a booked trip row, per journey (one enquiries query per pass).
    private static async Task<Dictionary<Guid, IReadOnlySet<int>>> BookedLegsAsync(
        ApplicationDbContext db, IReadOnlyList<SavedJourney> journeys, CancellationToken ct)
    {
        var tripIds = journeys.Where(j => j.TripId != null).Select(j => j.TripId!.Value).Distinct().ToList();
        if (tripIds.Count == 0) return new Dictionary<Guid, IReadOnlySet<int>>();
        var rows = await db.TripEnquiries.AsNoTracking()
            .Where(e => e.TripId != null && tripIds.Contains(e.TripId.Value) && e.Kind == TripEnquiryKinds.Taxi && e.Day != null
                        && TripEnquiryStatuses.Booked.Contains(e.Status))
            .ToListAsync(ct);
        return rows
            .Select(e => TripEnquiryContexts.Read(e.ContextJson))
            .Where(c => c.JourneyId != null && c.LegIndex != null)
            .GroupBy(c => c.JourneyId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<int>)g.Select(c => c.LegIndex!.Value).ToHashSet());
    }

    // Mirrors TripTimerService.TravelEvePassAsync: a Planned, unmuted trip whose travel day for this segment journey
    // falls on the journey's own date. Any other journey (Draft trip, muted, date moved) keeps its own eve push.
    internal static bool TripEveCovers(Trip trip, TripPlan plan, SavedJourney journey)
    {
        if (trip.OwnerUserId != journey.UserId || trip.Status != TripStatuses.Planned || trip.MutedAt != null) return false;
        if (trip.StartDate is not { } start || trip.EndDate is not { } end) return false;
        if (plan.Legs is not { Count: > 1 }) return false;
        if (TripJourneyLinks.Resolve(plan, journey.Id) is not { Role: TripStepRoles.Segment, TravelDay: { } day }
            || !plan.IsTravelDay(day)) return false;
        var date = TripExecutionRules.NightDate(start, day);
        return date == journey.TravelDate && date <= end;
    }

    // Applies availability to the legs; returns true when a leg changed.
    // segmentIds: journeys that are travel between two cities; their eve is the trip's (step journeys keep this push).
    // bookedLegs: cars booked in the trip; their driver going off only marks the leg (soft line in the app), no backup push.
    internal static bool Evaluate(
        SavedJourney journey, Dictionary<Guid, JourneyTaxiInfo> taxis, DateOnly today, DateTime now, List<Pending> pushes,
        IReadOnlySet<Guid>? segmentIds = null, IReadOnlySet<int>? bookedLegs = null)
    {
        var dirty = false;
        foreach (var leg in journey.Legs.OrderBy(l => l.LegIndex))
        {
            var isPublic = taxis.TryGetValue(leg.DriverTaxiId, out var driver) && driver.IsPublic;
            var booked = bookedLegs?.Contains(leg.LegIndex) == true;
            if (!isPublic && leg.DriverAvailable)
            {
                leg.DriverAvailable = false;
                leg.UpdatedAt = now;
                dirty = true;
                if (booked) continue;
                var name = driver?.DriverName ?? "Your driver";
                pushes.Add(new Pending(
                    JourneyRules.PausedKey(journey.Id, leg.DriverTaxiId),
                    JourneyNotifications.DriverPaused(
                        journey.UserId, journey.Id, name, leg.FromName, leg.ToName, FirstLiveBackup(leg, taxis), journey.TripId),
                    journey.Id));
            }
            else if (isPublic && !leg.DriverAvailable)
            {
                leg.DriverAvailable = true;
                leg.UpdatedAt = now;
                dirty = true;
                if (booked) continue;
                pushes.Add(new Pending(
                    JourneyRules.BackKey(journey.Id, leg.DriverTaxiId, today),
                    JourneyNotifications.DriverBack(journey.UserId, journey.Id, driver!.DriverName, leg.FromName, leg.ToName, journey.TripId),
                    journey.Id));
            }
        }

        if (JourneyEveRules.InWindow(journey.TravelDate, now) && segmentIds?.Contains(journey.Id) != true)
        {
            var notAsked = journey.Legs.Count(l => l.LastEnquiryId == null);
            pushes.Add(new Pending(
                JourneyEveRules.Key(journey.Id, journey.TravelDate),
                JourneyNotifications.DayBefore(journey.UserId, journey.Id, journey.FromName, journey.ToName, notAsked),
                journey.Id));
        }
        return dirty;
    }

    private static string? FirstLiveBackup(SavedJourneyLeg leg, Dictionary<Guid, JourneyTaxiInfo> taxis)
    {
        foreach (var id in new[] { leg.Backup1TaxiId, leg.Backup2TaxiId })
            if (id is { } g && taxis.TryGetValue(g, out var t) && t.IsPublic) return t.DriverName;
        return null;
    }
}

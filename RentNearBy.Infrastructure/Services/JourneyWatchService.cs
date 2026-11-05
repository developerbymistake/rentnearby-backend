using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Every 10 min (first pass after 1 min), journeys with TravelDate >= today (IST) and not Completed:
//   a chosen driver no longer public -> leg unavailable, journey DriverUnavailable, push once (jrn:p:...)
//   the same driver public again     -> leg available, status recomputed, push once per day (jrn:b:...)
//   TravelDate is tomorrow           -> push once (jrn:e:...), not for travel between two cities of a trip (the plan covers it)
// A journey in a trip routes the paused/back pushes to the trip plan.
public class JourneyWatchService(IServiceScopeFactory scopeFactory, ILogger<JourneyWatchService> logger) : BackgroundService
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

            var now = DateTime.UtcNow;
            var today = JourneyRules.TodayIst(now);
            var journeys = await repo.GetWatchedAsync(today, Batch);
            if (journeys.Count == 0) return;

            var taxiIds = journeys
                .SelectMany(j => j.Legs)
                .SelectMany(l => new[] { l.DriverTaxiId, l.Backup1TaxiId ?? Guid.Empty, l.Backup2TaxiId ?? Guid.Empty })
                .Where(id => id != Guid.Empty)
                .ToHashSet();
            var taxis = await repo.LoadTaxisAsync(taxiIds);
            var segmentIds = await SegmentJourneyIdsAsync(
                sp.GetRequiredService<ApplicationDbContext>(), journeys.Where(j => j.TravelDate == today.AddDays(1)).ToList(), ct);

            int changed = 0, sent = 0;
            foreach (var journey in journeys)
            {
                if (ct.IsCancellationRequested) break;
                var pushes = new List<Pending>();
                var dirty = Evaluate(journey, taxis, today, now, pushes, segmentIds);
                if (dirty)
                {
                    journey.Status = JourneyRules.JourneyStatusOf(journey.Legs);
                    journey.UpdatedAt = now;
                    try
                    {
                        await repo.SaveChangesAsync();
                        changed++;
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        // The stale row stays tracked and would fail every later save: end this pass, the next one looks again.
                        pushes.Clear();
                        logger.LogInformation("journey.watch concurrent {JourneyId}", journey.Id);
                        break;
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

            if (changed + sent > 0) logger.LogInformation("journey.watch changed={Changed} pushes={Pushes}", changed, sent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "journey.watch failed");
        }
    }

    // Journeys of tomorrow that are travel between two cities of their trip (one trips query per pass).
    private static async Task<HashSet<Guid>> SegmentJourneyIdsAsync(
        ApplicationDbContext db, IReadOnlyList<SavedJourney> tomorrow, CancellationToken ct)
    {
        var linked = tomorrow.Where(j => j.TripId != null).ToList();
        if (linked.Count == 0) return new HashSet<Guid>();
        var tripIds = linked.Select(j => j.TripId!.Value).Distinct().ToList();
        var trips = await db.Trips.AsNoTracking().Where(t => tripIds.Contains(t.Id)).ToListAsync(ct);
        var plans = trips.ToDictionary(t => t.Id, t => (t.OwnerUserId, Plan: TripPlanReader.Read(t)));
        return linked
            .Where(j => plans.TryGetValue(j.TripId!.Value, out var p) && p.OwnerUserId == j.UserId
                && TripJourneyLinks.Resolve(p.Plan, j.Id)?.Role == TripStepRoles.Segment)
            .Select(j => j.Id)
            .ToHashSet();
    }

    // Applies availability to the legs; returns true when a leg changed.
    // segmentIds: journeys that are travel between two cities; their eve is the trip's (step journeys keep this push).
    internal static bool Evaluate(
        SavedJourney journey, Dictionary<Guid, JourneyTaxiInfo> taxis, DateOnly today, DateTime now, List<Pending> pushes,
        IReadOnlySet<Guid>? segmentIds = null)
    {
        var dirty = false;
        foreach (var leg in journey.Legs.OrderBy(l => l.LegIndex))
        {
            var isPublic = taxis.TryGetValue(leg.DriverTaxiId, out var driver) && driver.IsPublic;
            if (!isPublic && leg.DriverAvailable)
            {
                leg.DriverAvailable = false;
                leg.UpdatedAt = now;
                dirty = true;
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
                pushes.Add(new Pending(
                    JourneyRules.BackKey(journey.Id, leg.DriverTaxiId, today),
                    JourneyNotifications.DriverBack(journey.UserId, journey.Id, driver!.DriverName, leg.FromName, leg.ToName, journey.TripId),
                    journey.Id));
            }
        }

        if (journey.TravelDate == today.AddDays(1) && segmentIds?.Contains(journey.Id) != true)
        {
            var notAsked = journey.Legs.Count(l => l.LastEnquiryId == null);
            pushes.Add(new Pending(
                JourneyRules.EveKey(journey.Id),
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

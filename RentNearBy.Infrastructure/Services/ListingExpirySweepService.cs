using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Sweep of live rooms/plots past their own ValidUntil, at the next due ValidUntil and at least hourly (:15 UTC). Each batch is one
// conditional UPDATE ... RETURNING that repeats "ValidUntil" < now(), so a renew or Go Live that moved
// ValidUntil first is never switched off.
public class ListingExpirySweepService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IRabbitMqPublisher _rabbitMqPublisher;
    private readonly ILogger<ListingExpirySweepService> _logger;
    private const int PageSize = 200;
    private const int AnchorMinute = 15;

    private sealed class ExpiredRow
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public DateTime ValidUntil { get; set; }
    }

    public ListingExpirySweepService(
        IServiceScopeFactory serviceScopeFactory,
        IRabbitMqPublisher rabbitMqPublisher,
        ILogger<ListingExpirySweepService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _rabbitMqPublisher = rabbitMqPublisher;
        _logger = logger;
    }

    // Runs at startup, then at the earliest live ValidUntil (so a lapsed row is hidden within seconds, not up to an hour
    // later), and at least every hour at :15 UTC for the plan-grace cases.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ListingExpirySweepService starting");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSweepAsync();
                var now = DateTime.UtcNow;
                var wake = SweepSchedule.NextWakeUtc(
                    now, SweepSchedule.NextHourlyAnchorUtc(now, AnchorMinute), await EarliestDueAsync());
                _logger.LogInformation("Next listing expiry sweep scheduled for {NextRunTime}", wake);
                var delay = wake - DateTime.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ListingExpirySweepService cancellation requested");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in ListingExpirySweepService");
            throw;
        }
    }

    private async Task RunSweepAsync()
    {
        _logger.LogInformation("=== LISTING EXPIRY SWEEP STARTED ===");
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var roomsExpired = await SweepAsync(sp, "\"RoomListings\"", ListingKinds.Room);
            var plotsExpired = await SweepAsync(sp, "\"PlotListings\"", ListingKinds.Plot);

            _logger.LogInformation(
                "Listing expiry sweep completed — roomsExpired={Rooms}, plotsExpired={Plots}",
                roomsExpired, plotsExpired);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in ListingExpirySweepService sweep");
        }
        _logger.LogInformation("=== LISTING EXPIRY SWEEP COMPLETED ===");
    }

    // table and kind are fixed whitelist values, never user input. Rows of an owner whose plan is still
    // Active and not more than 2 hours past EndsAt are left to PlanLifecycleService, which keeps the newest live
    // and plan-pauses the rest. The grace covers the gap to its next hourly tick; a plan it keeps failing to
    // expire loses the shield after that.
    // A lapsed row keeps PauseReason NULL (expired, not paused): it is never auto-resumed, only Go Live renews it.
    private async Task<int> SweepAsync(IServiceProvider sp, string table, string kind)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var totalExpired = 0;
        var owners = new HashSet<Guid>();
        var noActivePlan = $"""
                NOT EXISTS (SELECT 1 FROM "UserPlans" up
                            WHERE up."UserId" = t."UserId" AND up."FeatureKey" = '{kind}' AND up."Status" = 'Active'
                              AND up."EndsAt" > now() - (CASE WHEN up."PlaySubscriptionId" IS NULL
                                                               THEN interval '2 hours' ELSE interval '72 hours' END))
                """;
        var sql = $"""
                UPDATE {table} t SET "IsActive" = false, "PauseReason" = NULL, "UpdatedAt" = now()
                WHERE t."Id" IN (
                    SELECT t."Id" FROM {table} t
                    WHERE t."IsActive" = true AND t."IsDeleted" = false AND t."ValidUntil" < now()
                      AND {noActivePlan}
                    LIMIT {PageSize})
                  AND t."IsActive" = true AND t."ValidUntil" < now()
                  AND {noActivePlan}
                RETURNING t."Id", t."UserId", t."ValidUntil"
                """;
        while (true)
        {
            var batch = await db.Database.SqlQueryRaw<ExpiredRow>(sql).ToListAsync();

            foreach (var item in batch)
            {
                owners.Add(item.UserId);
                await PublishExpiredAsync(item.UserId, item.Id, kind, item.ValidUntil);
                totalExpired++;
            }

            if (batch.Count < PageSize) break;
        }

        if (owners.Count > 0)
            await ResumeFreedSlotsAsync(sp, db, owners, kind);

        return totalExpired;
    }

    // Each lapsed row freed a slot: per owner, a short transaction under the owner lock resumes the newest
    // plan-paused rows into it, like every other slot-freeing path. Caches are busted after commit.
    private async Task ResumeFreedSlotsAsync(IServiceProvider sp, ApplicationDbContext db, IReadOnlyCollection<Guid> owners, string kind)
    {
        var slots = sp.GetRequiredService<ILiveSlotService>();
        foreach (var userId in owners)
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await slots.LockOwnerAsync(userId);
                var resumed = await slots.ResumeAfterSlotFreedAsync(userId, kind);
                await tx.CommitAsync();
                if (resumed > 0)
                    _logger.LogInformation("Expiry sweep resumed {Resumed} plan-paused {Kind} rows for {UserId}", resumed, kind, userId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Expiry sweep: resume after freed slot failed for {UserId} ({Kind})", userId, kind);
            }
        }

        try
        {
            await sp.GetRequiredService<IListingCacheInvalidator>().BustForUsersAsync(owners, kind);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Expiry sweep cache bust failed ({Kind})", kind);
        }
    }

    private async Task PublishExpiredAsync(Guid userId, Guid listingId, string kind, DateTime expiredAt)
    {
        var payload = JsonSerializer.Serialize(new ListingExpiredMessage
        {
            UserId = userId,
            ListingId = listingId,
            ListingKind = kind,
            ExpiredAt = expiredAt,
        });

        try
        {
            await _rabbitMqPublisher.PublishAsync("listing.expired", payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Main queue publish failed for listing {ListingId} — trying DLQ", listingId);
            try
            {
                await _rabbitMqPublisher.PublishAsync("dlq.listing.expired", payload);
            }
            catch (Exception dlqEx)
            {
                _logger.LogError(dlqEx, "DLQ publish also failed for listing {ListingId} — notification will be missed", listingId);
            }
        }
    }

    // The earliest ValidUntil still ahead among live rooms and plots; null when none or on error (the anchor then rules).
    private async Task<DateTime?> EarliestDueAsync()
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.Database.SqlQueryRaw<DateTime?>("""
                SELECT MIN(m) AS "Value" FROM (
                    SELECT MIN("ValidUntil") AS m FROM "RoomListings"
                    WHERE "IsActive" = true AND "IsDeleted" = false AND "ValidUntil" >= now()
                    UNION ALL
                    SELECT MIN("ValidUntil") AS m FROM "PlotListings"
                    WHERE "IsActive" = true AND "IsDeleted" = false AND "ValidUntil" >= now()) x
                """).ToListAsync();
            return rows.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing expiry sweep: next due lookup failed");
            return null;
        }
    }
}

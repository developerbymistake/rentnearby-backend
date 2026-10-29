using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Hourly (anchored at :15 UTC) sweep of live rooms/plots past their own ValidUntil. Each batch is one
// conditional UPDATE ... RETURNING that repeats "ValidUntil" < now(), so a renew or Go Live that moved
// ValidUntil first is never switched off.
public class ListingExpirySweepService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IRabbitMqPublisher _rabbitMqPublisher;
    private readonly ILogger<ListingExpirySweepService> _logger;
    private PeriodicTimer? _timer;
    private const int PageSize = 200;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ListingExpirySweepService starting");
        try
        {
            var nextRunTime = GetNextRunTime();
            var initialDelay = nextRunTime - DateTime.UtcNow;
            if (initialDelay < TimeSpan.Zero) initialDelay = TimeSpan.Zero;

            _logger.LogInformation("Next listing expiry sweep scheduled for {NextRunTime}", nextRunTime);
            await Task.Delay(initialDelay, stoppingToken);

            _timer = new PeriodicTimer(Interval);
            await RunSweepAsync();
            while (await _timer.WaitForNextTickAsync(stoppingToken))
                await RunSweepAsync();
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
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
    // Active are left to PlanLifecycleService, which keeps the newest live and plan-pauses the rest.
    // A lapsed row keeps PauseReason NULL (expired, not paused): it is never auto-resumed, only Go Live renews it.
    private async Task<int> SweepAsync(IServiceProvider sp, string table, string kind)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var totalExpired = 0;
        var owners = new HashSet<Guid>();
        var noActivePlan = $"""
                NOT EXISTS (SELECT 1 FROM "UserPlans" up
                            WHERE up."UserId" = t."UserId" AND up."FeatureKey" = '{kind}' AND up."Status" = 'Active')
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

    private static DateTime GetNextRunTime()
    {
        var now = DateTime.UtcNow;
        var target = new DateTime(now.Year, now.Month, now.Day, now.Hour, AnchorMinute, 0, DateTimeKind.Utc);
        return now < target ? target : target.AddHours(1);
    }
}

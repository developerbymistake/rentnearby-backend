using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Replaces MembershipExpiryService + PlotMembershipExpiryService — there is no membership record to
// expire under the coin model, so this sweeps RoomListing/PlotListing directly by their own
// ValidUntil, one unified daily job instead of two near-identical ones per listing kind.
public class ListingExpirySweepService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IRabbitMqPublisher _rabbitMqPublisher;
    private readonly ILogger<ListingExpirySweepService> _logger;
    private PeriodicTimer? _timer;
    private const int PageSize = 200;

    // 18:30 UTC = 00:00 IST — same schedule the old membership-expiry services ran on.
    private static readonly TimeSpan RunTimeUtc = new(18, 30, 0);

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
            if (initialDelay.TotalMilliseconds <= 0)
                initialDelay = TimeSpan.FromHours(24);

            _logger.LogInformation("Next listing expiry sweep scheduled for {NextRunTime}", nextRunTime);
            await Task.Delay(initialDelay, stoppingToken);

            _timer = new PeriodicTimer(TimeSpan.FromHours(24));
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
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;

            var roomsExpired = await SweepAsync(db, now, isRoom: true);
            var plotsExpired = await SweepAsync(db, now, isRoom: false);

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

    private async Task<int> SweepAsync(ApplicationDbContext db, DateTime now, bool isRoom)
    {
        var totalExpired = 0;
        while (true)
        {
            // Processed rows drop out of the next iteration's query automatically (they no longer
            // match IsActive == true), same paging trick MembershipExpiryService used.
            var batch = isRoom
                ? await db.RoomListings
                    .Where(l => l.IsActive && l.ValidUntil != null && l.ValidUntil < now)
                    .Take(PageSize)
                    .Select(l => new { l.UserId, l.Id, ValidUntil = l.ValidUntil!.Value })
                    .ToListAsync()
                : await db.PlotListings
                    .Where(p => p.IsActive && p.ValidUntil != null && p.ValidUntil < now)
                    .Take(PageSize)
                    .Select(p => new { p.UserId, p.Id, ValidUntil = p.ValidUntil!.Value })
                    .ToListAsync();

            if (batch.Count == 0) break;

            var ids = batch.Select(b => b.Id).ToList();
            if (isRoom)
                await db.RoomListings.Where(l => ids.Contains(l.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsActive, false).SetProperty(l => l.UpdatedAt, now));
            else
                await db.PlotListings.Where(p => ids.Contains(p.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false).SetProperty(p => p.UpdatedAt, now));

            foreach (var item in batch)
            {
                await PublishExpiredAsync(item.UserId, item.Id, isRoom ? ListingKinds.Room : ListingKinds.Plot, item.ValidUntil);
                totalExpired++;
            }

            if (batch.Count < PageSize) break;
        }

        return totalExpired;
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
        var todayTarget = now.Date.Add(RunTimeUtc);
        return now < todayTarget ? todayTarget : todayTarget.AddDays(1);
    }
}

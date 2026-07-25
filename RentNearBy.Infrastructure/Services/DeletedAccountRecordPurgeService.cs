using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Purges DeletedAccountRecords past their 180-day retention window (IT Rules 2021, Rule 3(1)(j)).
// Its own dedicated daily job — deliberately separate from ListingExpirySweepService/
// DistrictDigestJobService so future changes here can't affect either existing schedule.
public class DeletedAccountRecordPurgeService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<DeletedAccountRecordPurgeService> _logger;
    private PeriodicTimer? _timer;
    private const int RetentionDays = 180;

    // 23:30 UTC (previous day) = 05:00 IST (UTC+5:30) — one hour clear of
    // DistrictDigestJobService's 4:00 AM IST slot.
    private static readonly TimeSpan RunTimeUtc = new(23, 30, 0);

    public DeletedAccountRecordPurgeService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<DeletedAccountRecordPurgeService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeletedAccountRecordPurgeService starting");
        try
        {
            var nextRunTime = GetNextRunTime();
            var initialDelay = nextRunTime - DateTime.UtcNow;
            if (initialDelay.TotalMilliseconds <= 0)
                initialDelay = TimeSpan.FromHours(24);

            _logger.LogInformation("Next deleted-account purge scheduled for {NextRunTime}", nextRunTime);
            await Task.Delay(initialDelay, stoppingToken);

            _timer = new PeriodicTimer(TimeSpan.FromHours(24));
            await RunPurgeAsync();
            while (await _timer.WaitForNextTickAsync(stoppingToken))
                await RunPurgeAsync();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("DeletedAccountRecordPurgeService cancellation requested");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in DeletedAccountRecordPurgeService");
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

    private async Task RunPurgeAsync()
    {
        _logger.LogInformation("=== DELETED ACCOUNT RECORD PURGE STARTED ===");
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);

            var purged = await db.DeletedAccountRecords.Where(r => r.DeletedAt < cutoff).ExecuteDeleteAsync();

            _logger.LogInformation("Deleted account record purge completed — purged={Purged}", purged);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in DeletedAccountRecordPurgeService sweep");
        }
        _logger.LogInformation("=== DELETED ACCOUNT RECORD PURGE COMPLETED ===");
    }

    private static DateTime GetNextRunTime()
    {
        var now = DateTime.UtcNow;
        var todayTarget = now.Date.Add(RunTimeUtc);
        return now < todayTarget ? todayTarget : todayTarget.AddDays(1);
    }
}

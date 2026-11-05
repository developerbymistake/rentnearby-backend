using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

// Hourly: inbox rows older than NotificationInbox.RetentionDays are deleted in batches until none is left
// (their NotificationReads go with them via the cascade FK).
public class NotificationRetentionService(IServiceScopeFactory scopeFactory, ILogger<NotificationRetentionService> logger)
    : BackgroundService
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

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
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var cutoff = DateTime.UtcNow.AddDays(-NotificationInbox.RetentionDays);
            var total = 0;
            int deleted;
            do
            {
                deleted = await unitOfWork.Notifications.PurgeOlderThanAsync(cutoff, NotificationInbox.PurgeBatch);
                total += deleted;
            }
            while (deleted == NotificationInbox.PurgeBatch && !ct.IsCancellationRequested);
            if (total > 0) logger.LogInformation("notifications.purged {Count}", total);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Notification retention pass failed");
        }
    }
}

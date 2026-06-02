using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class PlotMembershipExpiryService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IRabbitMqPublisher _rabbitMqPublisher;
    private readonly ILogger<PlotMembershipExpiryService> _logger;
    private PeriodicTimer? _timer;

    public PlotMembershipExpiryService(
        IServiceScopeFactory serviceScopeFactory,
        IRabbitMqPublisher rabbitMqPublisher,
        ILogger<PlotMembershipExpiryService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _rabbitMqPublisher = rabbitMqPublisher ?? throw new ArgumentNullException(nameof(rabbitMqPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PlotMembershipExpiryService starting");

        try
        {
            var nextRunTime = GetNextRunTime();
            var initialDelay = nextRunTime - DateTime.UtcNow;

            if (initialDelay.TotalMilliseconds <= 0)
                initialDelay = TimeSpan.FromHours(24);

            _logger.LogInformation("Next plot membership expiry check scheduled for {NextRunTime}", nextRunTime);

            await Task.Delay(initialDelay, stoppingToken);

            _timer = new PeriodicTimer(TimeSpan.FromHours(24));

            await ProcessPlotMembershipExpiryAsync(stoppingToken);

            while (await _timer.WaitForNextTickAsync(stoppingToken))
                await ProcessPlotMembershipExpiryAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("PlotMembershipExpiryService cancellation requested");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in PlotMembershipExpiryService");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PlotMembershipExpiryService stopping");
        _timer?.Dispose();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }

    private async Task ProcessPlotMembershipExpiryAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== PLOT MEMBERSHIP EXPIRY JOB STARTED ===");

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // AddDays(1) so query captures memberships expiring today (ValidUntil < tomorrow midnight)
            var expiredDate = DateTime.UtcNow.Date.AddDays(1);
            var now = DateTime.UtcNow;
            const int pageSize = 200;
            var totalProcessed = 0;
            var totalDisabled = 0;

            while (true)
            {
                var batch = await unitOfWork.PlotMemberships.GetExpiredPagedAsync(expiredDate, 1, pageSize);
                if (batch.Count == 0) break;

                _logger.LogInformation("Processing batch of {BatchCount} expired plot memberships", batch.Count);

                foreach (var membership in batch)
                {
                    try
                    {
                        membership.IsActive = false;
                        membership.UpdatedAt = now;

                        var userPlotListings = await unitOfWork.PlotListings.GetActiveByUserIdAsync(membership.UserId);
                        foreach (var plot in userPlotListings.Where(p => !p.IsDeleted))
                        {
                            plot.IsActive = false;
                            plot.UpdatedAt = now;
                            totalDisabled++;
                        }

                        totalProcessed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing plot membership {MembershipId}", membership.Id);
                    }
                }

                try
                {
                    await unitOfWork.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving plot batch changes");
                    throw;
                }

                foreach (var membership in batch)
                {
                    try
                    {
                        await _rabbitMqPublisher.PublishAsync("membership.expired",
                            JsonSerializer.Serialize(new MembershipExpiredMessage
                            {
                                UserId    = membership.UserId,
                                Type      = "plot",
                                ExpiredAt = membership.ValidUntil
                            }));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to publish plot expiry event for membership {MembershipId}", membership.Id);
                    }
                }

                if (batch.Count < pageSize) break;
            }

            _logger.LogInformation(
                "Plot membership expiry job completed — processed={Processed}, disabledPlots={Disabled}",
                totalProcessed, totalDisabled);
            _logger.LogInformation("=== PLOT MEMBERSHIP EXPIRY JOB COMPLETED ===");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("PlotMembershipExpiryService job cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in ProcessPlotMembershipExpiryAsync");
        }
    }

    // 19:30 UTC = 01:00 IST — runs 1 hour after room expiry job (18:30 UTC)
    private static readonly TimeSpan RunTimeUtc = new(19, 30, 0);

    private static DateTime GetNextRunTime()
    {
        var now = DateTime.UtcNow;
        var todayTarget = now.Date.Add(RunTimeUtc);
        return now < todayTarget ? todayTarget : todayTarget.AddDays(1);
    }
}

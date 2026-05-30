using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class PlotMembershipExpiryService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<PlotMembershipExpiryService> _logger;
    private PeriodicTimer? _timer;

    public PlotMembershipExpiryService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<PlotMembershipExpiryService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
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
            var expiredMemberships = await unitOfWork.PlotMemberships.GetExpiredAsync(expiredDate);
            var expiredList = expiredMemberships.ToList();

            if (expiredList.Count == 0)
            {
                _logger.LogInformation("No expired plot memberships found");
                _logger.LogInformation("=== PLOT MEMBERSHIP EXPIRY JOB COMPLETED ===");
                return;
            }

            _logger.LogInformation("Found {ExpiredMembershipCount} expired plot memberships", expiredList.Count);

            var disabledPlotListingsCount = 0;
            var now = DateTime.UtcNow;

            foreach (var membership in expiredList)
            {
                try
                {
                    membership.IsActive = false;
                    membership.UpdatedAt = now;

                    _logger.LogInformation(
                        "Deactivating plot membership {MembershipId} for user {UserId} (ValidUntil: {ValidUntil})",
                        membership.Id, membership.UserId, membership.ValidUntil);

                    var userPlotListings = await unitOfWork.PlotListings.GetActiveByUserIdAsync(membership.UserId);
                    foreach (var plot in userPlotListings.Where(p => !p.IsDeleted))
                    {
                        plot.IsActive = false;
                        plot.UpdatedAt = now;
                        disabledPlotListingsCount++;

                        _logger.LogInformation("Disabled plot {PlotId} for user {UserId}", plot.Id, membership.UserId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing plot membership {MembershipId}. Continuing with next", membership.Id);
                }
            }

            try
            {
                await unitOfWork.SaveChangesAsync();
                _logger.LogInformation(
                    "PlotListing expiry job completed. Processed: {ProcessedCount} memberships, Disabled: {DisabledCount} plots",
                    expiredList.Count, disabledPlotListingsCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving plot expiry changes to database");
                throw;
            }

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

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class MembershipExpiryService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IRabbitMqPublisher _rabbitMqPublisher;
    private readonly ILogger<MembershipExpiryService> _logger;
    private PeriodicTimer? _timer;

    public MembershipExpiryService(
        IServiceScopeFactory serviceScopeFactory,
        IRabbitMqPublisher rabbitMqPublisher,
        ILogger<MembershipExpiryService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _rabbitMqPublisher = rabbitMqPublisher ?? throw new ArgumentNullException(nameof(rabbitMqPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MembershipExpiryService starting");

        try
        {
            // Calculate initial delay to next 12:00 AM UTC
            var nextRunTime = GetNextRunTime();
            var initialDelay = nextRunTime - DateTime.UtcNow;

            if (initialDelay.TotalMilliseconds <= 0)
            {
                initialDelay = TimeSpan.FromHours(24);
            }

            _logger.LogInformation("Next membership expiry check scheduled for {NextRunTime}", nextRunTime);
            _logger.LogInformation("Initial delay: {DelayHours} hours", Math.Round(initialDelay.TotalHours, 2));

            // Wait for initial run time
            await Task.Delay(initialDelay, stoppingToken);

            // Create timer: runs every 24 hours after first run
            _timer = new PeriodicTimer(TimeSpan.FromHours(24));

            // Run immediately and then every 24 hours
            await ProcessMembershipExpiryAsync(stoppingToken);

            while (await _timer.WaitForNextTickAsync(stoppingToken))
            {
                await ProcessMembershipExpiryAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MembershipExpiryService cancellation requested");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in MembershipExpiryService");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MembershipExpiryService stopping");

        if (_timer is not null)
        {
            _timer.Dispose();
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// PROFESSIONAL PATTERN:
    /// - Uses IServiceScopeFactory to create isolated DB context
    /// - Each background job gets its own scope (clean DI)
    /// - Proper async/await throughout
    /// - Structured logging with context
    /// - Graceful error handling (continues on individual failures)
    /// </summary>
    private async Task ProcessMembershipExpiryAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== MEMBERSHIP EXPIRY JOB STARTED ===");

        try
        {
            // Create new scope for this job execution
            // This ensures clean database context and proper cleanup
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // AddDays(1) so query captures memberships expiring today (ValidUntil < tomorrow midnight)
            var expiredDate = DateTime.UtcNow.Date.AddDays(1);
            var now = DateTime.UtcNow;
            const int pageSize = 200;
            var totalProcessed = 0;
            var totalDisabled = 0;

            // Page-by-page processing — keeps memory bounded to 200 records at a time.
            // After each save, processed records are IsActive=false so they drop out of the
            // next query automatically; we always query "page 1" of the remaining set.
            while (true)
            {
                var batch = await unitOfWork.RoomMemberships.GetExpiredPagedAsync(expiredDate, 1, pageSize);
                if (batch.Count == 0) break;

                _logger.LogInformation("Processing batch of {BatchCount} expired memberships", batch.Count);

                foreach (var membership in batch)
                {
                    try
                    {
                        membership.IsActive = false;
                        membership.UpdatedAt = now;

                        var userListings = await unitOfWork.RoomListings.GetActiveByUserIdAsync(membership.UserId);
                        foreach (var listing in userListings.Where(l => !l.IsDeleted))
                        {
                            listing.IsActive = false;
                            listing.UpdatedAt = now;
                            totalDisabled++;
                        }

                        totalProcessed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing membership {MembershipId}", membership.Id);
                    }
                }

                try
                {
                    await unitOfWork.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving batch changes");
                    throw;
                }

                // Publish expiry events for this batch — fire-and-forget
                foreach (var membership in batch)
                {
                    try
                    {
                        await _rabbitMqPublisher.PublishAsync("membership.expired",
                            JsonSerializer.Serialize(new MembershipExpiredMessage
                            {
                                UserId    = membership.UserId,
                                Type      = "room",
                                ExpiredAt = membership.ValidUntil
                            }));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Main queue publish failed for membership {MembershipId} — trying DLQ", membership.Id);
                        try
                        {
                            await _rabbitMqPublisher.PublishAsync("dlq.membership.expired",
                                JsonSerializer.Serialize(new MembershipExpiredMessage
                                {
                                    UserId    = membership.UserId,
                                    Type      = "room",
                                    ExpiredAt = membership.ValidUntil
                                }));
                        }
                        catch (Exception dlqEx)
                        {
                            _logger.LogError(dlqEx, "DLQ publish also failed for membership {MembershipId} — notification will be missed", membership.Id);
                        }
                    }
                }

                if (batch.Count < pageSize) break;
            }

            _logger.LogInformation(
                "Membership expiry job completed — processed={Processed}, disabledListings={Disabled}",
                totalProcessed, totalDisabled);
            _logger.LogInformation("=== MEMBERSHIP EXPIRY JOB COMPLETED ===");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MembershipExpiryService job cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in ProcessMembershipExpiryAsync");
        }
    }

    /// <summary>
    /// Calculate next run time at 12:00 AM UTC.
    /// Professional approach: uses UTC for consistency across timezones.
    ///
    /// For local time, use DateTime.Now instead of DateTime.UtcNow.
    /// </summary>
    // 18:30 UTC = 00:00 IST (UTC+5:30)
    private static readonly TimeSpan RunTimeUtc = new(18, 30, 0);

    private static DateTime GetNextRunTime()
    {
        var now = DateTime.UtcNow;
        var todayTarget = now.Date.Add(RunTimeUtc);
        return now < todayTarget ? todayTarget : todayTarget.AddDays(1);
    }
}

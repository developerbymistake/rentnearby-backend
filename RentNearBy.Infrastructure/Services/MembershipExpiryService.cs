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
            var expiredMemberships = await unitOfWork.RoomMemberships.GetExpiredAsync(expiredDate);
            var expiredList = expiredMemberships.ToList();

            if (expiredList.Count == 0)
            {
                _logger.LogInformation("No expired memberships found");
                _logger.LogInformation("=== MEMBERSHIP EXPIRY JOB COMPLETED ===");
                return;
            }

            _logger.LogInformation("Found {ExpiredMembershipCount} expired memberships", expiredList.Count);

            var disabledListingsCount = 0;
            var now = DateTime.UtcNow;

            // Process each expired membership
            foreach (var membership in expiredList)
            {
                try
                {
                    // Mark membership as inactive
                    membership.IsActive = false;
                    membership.UpdatedAt = now;

                    _logger.LogInformation(
                        "Deactivating membership {MembershipId} for user {UserId} (ValidUntil: {ValidUntil})",
                        membership.Id, membership.UserId, membership.ValidUntil);

                    // Get and disable all active listings for this user
                    var userListings = await unitOfWork.RoomListings.GetActiveByUserIdAsync(membership.UserId);
                    var listingsToDisable = userListings.Where(l => !l.IsDeleted).ToList();

                    foreach (var listing in listingsToDisable)
                    {
                        listing.IsActive = false;
                        listing.UpdatedAt = now;
                        disabledListingsCount++;

                        _logger.LogInformation(
                            "Disabled listing {RoomListingId} (Room: {RoomType}, City: {City})",
                            listing.Id,
                            listing.RoomType?.Name ?? "Unknown",
                            listing.City?.Name ?? "Unknown");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing membership {MembershipId}. Continuing with next membership", membership.Id);
                    // Continue processing other memberships even if one fails
                }
            }

            // Save all changes in a single transaction (atomic operation)
            try
            {
                await unitOfWork.SaveChangesAsync();

                _logger.LogInformation(
                    "Successfully completed membership expiry job. " +
                    "Processed: {ProcessedCount} memberships, Disabled: {DisabledCount} listings",
                    expiredList.Count,
                    disabledListingsCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving changes to database");
                throw;
            }

            // Publish expiry events — fire-and-forget (RabbitMQ failure must not block expiry job)
            foreach (var membership in expiredList)
            {
                try
                {
                    var message = JsonSerializer.Serialize(new MembershipExpiredMessage
                    {
                        UserId = membership.UserId,
                        Type = "room",
                        ExpiredAt = membership.ValidUntil
                    });
                    await _rabbitMqPublisher.PublishAsync("membership.expired", message);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to publish expiry event for membership {MembershipId}", membership.Id);
                }
            }

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

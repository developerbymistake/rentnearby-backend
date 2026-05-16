using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class MembershipExpiryService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MembershipExpiryService> _logger;
    private Timer? _timer;

    public MembershipExpiryService(IServiceProvider serviceProvider, ILogger<MembershipExpiryService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MembershipExpiryService started");

        // Calculate delay until next 12:00 AM
        var now = DateTime.Now;
        var nextRun = now.Date.AddDays(1).AddHours(0); // 12:00 AM next day
        var delay = nextRun - now;

        if (delay.TotalMilliseconds <= 0)
        {
            delay = TimeSpan.FromHours(24);
        }

        _logger.LogInformation($"Next membership expiry check scheduled for: {nextRun}");

        // First run: wait until 12 AM
        await Task.Delay(delay, stoppingToken);

        // Then run every 24 hours
        _timer = new Timer(async _ => await ProcessMembershipExpiry(stoppingToken), null, TimeSpan.Zero, TimeSpan.FromHours(24));

        // Keep service alive
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MembershipExpiryService stopping");
        _timer?.Dispose();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }

    private async Task ProcessMembershipExpiry(CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== MEMBERSHIP EXPIRY JOB STARTED ===");

        try
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                // Get all expired memberships
                var expiredDate = DateTime.UtcNow.Date; // Only expired (not today)
                var expiredMemberships = await unitOfWork.UserMemberships.GetExpiredAsync(expiredDate);
                var expiredList = expiredMemberships.ToList();

                if (!expiredList.Any())
                {
                    _logger.LogInformation("No expired memberships found");
                    _logger.LogInformation("=== MEMBERSHIP EXPIRY JOB COMPLETED ===\n");
                    return;
                }

                _logger.LogInformation($"Found {expiredList.Count} expired memberships. Processing...");

                int disabledListingsCount = 0;
                var now = DateTime.UtcNow;

                foreach (var membership in expiredList)
                {
                    try
                    {
                        // Mark membership as inactive
                        membership.IsActive = false;
                        membership.UpdatedAt = now;

                        _logger.LogInformation($"Marked membership {membership.Id} (User: {membership.UserId}, ValidUntil: {membership.ValidUntil}) as INACTIVE");

                        // Get all active listings for this user
                        var userListings = await unitOfWork.Listings.GetActiveByUserIdAsync(membership.UserId);
                        var listingsToDisable = userListings.Where(l => !l.IsDeleted).ToList();

                        foreach (var listing in listingsToDisable)
                        {
                            listing.IsActive = false;
                            listing.UpdatedAt = now;
                            disabledListingsCount++;

                            _logger.LogInformation(
                                $"  └─ Disabled listing {listing.Id} " +
                                $"(Room: {listing.RoomType?.Name}, City: {listing.City?.Name}, ValidUntil: {listing.ValidUntil})");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error processing membership {membership.Id}: {ex.Message}");
                    }
                }

                // Save all changes in one transaction
                await unitOfWork.SaveChangesAsync();

                _logger.LogInformation($"✅ Expiry job completed successfully");
                _logger.LogInformation($"   • Expired memberships processed: {expiredList.Count}");
                _logger.LogInformation($"   • Listings disabled: {disabledListingsCount}");
                _logger.LogInformation("=== MEMBERSHIP EXPIRY JOB COMPLETED ===\n");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ Error in membership expiry job: {ex.Message}");
            _logger.LogError($"Stack trace: {ex.StackTrace}");
            _logger.LogInformation("=== MEMBERSHIP EXPIRY JOB FAILED ===\n");
        }
    }
}

/// <summary>
/// Interface for user membership repository expiry queries
/// </summary>
public interface IUserMembershipRepositoryExpiry
{
    Task<IEnumerable<UserMembership>> GetExpiredAsync(DateTime beforeDate);
}

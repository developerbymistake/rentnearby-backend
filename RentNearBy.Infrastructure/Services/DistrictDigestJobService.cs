using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class DistrictDigestJobService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IRabbitMqPublisher _rabbitMqPublisher;
    private readonly ILogger<DistrictDigestJobService> _logger;
    private PeriodicTimer? _timer;

    public DistrictDigestJobService(
        IServiceScopeFactory serviceScopeFactory,
        IRabbitMqPublisher rabbitMqPublisher,
        ILogger<DistrictDigestJobService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _rabbitMqPublisher = rabbitMqPublisher ?? throw new ArgumentNullException(nameof(rabbitMqPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DistrictDigestJobService starting");

        try
        {
            // Calculate initial delay to next 4:00 AM IST
            var nextRunTime = GetNextRunTime();
            var initialDelay = nextRunTime - DateTime.UtcNow;

            if (initialDelay.TotalMilliseconds <= 0)
            {
                initialDelay = TimeSpan.FromHours(24);
            }

            _logger.LogInformation("Next district digest run scheduled for {NextRunTime}", nextRunTime);
            _logger.LogInformation("Initial delay: {DelayHours} hours", Math.Round(initialDelay.TotalHours, 2));

            // Wait for initial run time
            await Task.Delay(initialDelay, stoppingToken);

            // Create timer: runs every 24 hours after first run
            _timer = new PeriodicTimer(TimeSpan.FromHours(24));

            // Run immediately and then every 24 hours
            await ProcessDistrictDigestAsync(stoppingToken);

            while (await _timer.WaitForNextTickAsync(stoppingToken))
            {
                await ProcessDistrictDigestAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("DistrictDigestJobService cancellation requested");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in DistrictDigestJobService");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("DistrictDigestJobService stopping");

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

    private async Task ProcessDistrictDigestAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== DISTRICT DIGEST JOB STARTED ===");

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var pendingRooms = await unitOfWork.RoomListings.GetPendingDigestListingsAsync();
            var pendingPlots = await unitOfWork.PlotListings.GetPendingDigestListingsAsync();

            var districts = new Dictionary<Guid, (string Name, List<Guid> RoomIds, List<Guid> PlotIds)>();

            foreach (var r in pendingRooms)
            {
                if (!districts.TryGetValue(r.DistrictId, out var agg))
                    agg = (r.DistrictName, new List<Guid>(), new List<Guid>());
                agg.RoomIds.Add(r.Id);
                districts[r.DistrictId] = agg;
            }

            foreach (var p in pendingPlots)
            {
                if (!districts.TryGetValue(p.DistrictId, out var agg))
                    agg = (p.DistrictName, new List<Guid>(), new List<Guid>());
                agg.PlotIds.Add(p.Id);
                districts[p.DistrictId] = agg;
            }

            _logger.LogInformation("District digest job: {Count} districts with pending listings", districts.Count);

            foreach (var (districtId, agg) in districts)
            {
                var message = new DistrictDigestMessage
                {
                    DistrictId = districtId,
                    DistrictName = agg.Name,
                    RoomCount = agg.RoomIds.Count,
                    PlotCount = agg.PlotIds.Count
                };

                var published = false;
                try
                {
                    await _rabbitMqPublisher.PublishAsync("district.digest.ready", JsonSerializer.Serialize(message));
                    published = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Digest publish failed for district {DistrictId} ({DistrictName}) after retries — " +
                        "listings remain pending and will be retried in tomorrow's run", districtId, agg.Name);
                }

                if (!published) continue;

                // Mark independently per listing type — a failure here is distinct from a
                // publish failure: the notification has already gone out, so a mark failure
                // only risks a duplicate count tomorrow for that listing type, not a lost push.
                if (agg.RoomIds.Count > 0)
                {
                    try
                    {
                        await unitOfWork.RoomListings.MarkDigestNotifiedAsync(agg.RoomIds);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed to mark {Count} room listings as digested for district {DistrictId} — " +
                            "they will be double-counted in tomorrow's digest", agg.RoomIds.Count, districtId);
                    }
                }

                if (agg.PlotIds.Count > 0)
                {
                    try
                    {
                        await unitOfWork.PlotListings.MarkDigestNotifiedAsync(agg.PlotIds);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed to mark {Count} plot listings as digested for district {DistrictId} — " +
                            "they will be double-counted in tomorrow's digest", agg.PlotIds.Count, districtId);
                    }
                }
            }

            _logger.LogInformation("=== DISTRICT DIGEST JOB COMPLETED ===");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("DistrictDigestJobService job cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in ProcessDistrictDigestAsync");
        }
    }

    // 22:30 UTC (previous day) = 04:00 IST (UTC+5:30)
    private static readonly TimeSpan RunTimeUtc = new(22, 30, 0);

    private static DateTime GetNextRunTime()
    {
        var now = DateTime.UtcNow;
        var todayTarget = now.Date.Add(RunTimeUtc);
        return now < todayTarget ? todayTarget : todayTarget.AddDays(1);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;

// Fires DueActions rows. Safe on any number of instances (lease claim with SKIP LOCKED). Keeps running while the master
// switch is off, so timers, cancels and expiries are never stranded. Nothing but cancellation leaves ExecuteAsync.
public sealed class DueActionsWorker : BackgroundService
{
    public const string HeartbeatName = "dueActions";
    private static readonly TimeSpan DefaultErrorPause = TimeSpan.FromSeconds(5);

    private readonly Func<DueActionLoop> _loopFactory;
    private readonly IDueActionsWaiter _signal;
    private readonly ILogger _logger;
    private readonly WorkerHeartbeats? _heartbeats;
    private readonly TimeSpan _errorPause;

    public DueActionsWorker(
        IServiceScopeFactory scopeFactory, DueActionsSignal signal, ILogger<DueActionsWorker> logger,
        TaxiRequestMetrics? metrics = null, WorkerHeartbeats? heartbeats = null)
        : this(() => new DueActionLoop(new PostgresDueActionStore(scopeFactory), new DueActionItemScopeFactory(scopeFactory), signal, logger, metrics),
            signal, logger, heartbeats, DefaultErrorPause)
    {
    }

    internal DueActionsWorker(Func<DueActionLoop> loopFactory, IDueActionsWaiter signal, ILogger logger, WorkerHeartbeats? heartbeats, TimeSpan errorPause)
    {
        _loopFactory = loopFactory;
        _signal = signal;
        _logger = logger;
        _heartbeats = heartbeats;
        _errorPause = errorPause;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DueActionLoop? loop = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _heartbeats?.Beat(HeartbeatName);
                loop ??= _loopFactory();
                var pass = await loop.RunOnceAsync(stoppingToken);
                if (pass.Sleep > TimeSpan.Zero) await _signal.WaitAsync(pass.Sleep, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Due actions pass failed");
                try
                {
                    await Task.Delay(_errorPause, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}

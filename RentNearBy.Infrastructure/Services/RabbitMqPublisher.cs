using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class RabbitMqPublisher : IRabbitMqPublisher, IAsyncDisposable
{
    internal static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);

    // Whole call, retries and waits included. A retry after a timeout can deliver a message twice (at least once):
    // every try of one call carries the same BasicProperties.MessageId so consumers can drop duplicates.
    internal static readonly TimeSpan TotalBudget = TimeSpan.FromMilliseconds(3800);

    private readonly ILogger<RabbitMqPublisher> _logger;
    private readonly RabbitChannelGate _gate;
    private readonly Func<TimeSpan, Task> _delay;

    public RabbitMqPublisher(IConfiguration configuration, ILogger<RabbitMqPublisher> logger)
        : this(new RabbitConnectionChannelFactory(
                new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) }, logger),
            logger, Task.Delay)
    {
    }

    internal RabbitMqPublisher(IRabbitChannelFactory channelFactory, ILogger<RabbitMqPublisher> logger, Func<TimeSpan, Task> delay)
    {
        _logger = logger;
        _gate = new RabbitChannelGate(channelFactory);
        _delay = delay;
    }

    public async Task PublishAsync(string queue, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var messageId = Guid.NewGuid().ToString("N");
        var started = Stopwatch.GetTimestamp();
        using var total = new CancellationTokenSource(TotalBudget);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                timeout.CancelAfter(AttemptTimeout);
                try
                {
                    await _gate.PublishAsync(queue, body, messageId, timeout.Token);
                }
                catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException($"RabbitMQ publish to '{queue}' timed out", ex);
                }
                _logger.LogDebug("Published message to queue '{Queue}'", queue);
                return;
            }
            catch (Exception ex) when (attempt < RetryDelays.Length
                                       && TotalBudget - Stopwatch.GetElapsedTime(started) > RetryDelays[attempt])
            {
                var delay = RetryDelays[attempt];
                _logger.LogWarning(
                    "RabbitMQ publish retry {Attempt}/{Max} after {Delay}ms, queue='{Queue}': {Error}",
                    attempt + 1, RetryDelays.Length, delay.TotalMilliseconds, queue, ex.Message);
                await _delay(delay);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

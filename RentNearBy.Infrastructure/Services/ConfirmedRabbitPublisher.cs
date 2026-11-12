using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

// One long-lived publisher connection and channel with confirms and mandatory routing. Separate from the consumer connection.
// Topology is declared once per (re)connect; a publish never declares anything.
public sealed class ConfirmedRabbitPublisher : IConfirmedPublisher, IAsyncDisposable
{
    private readonly ILogger<ConfirmedRabbitPublisher> _logger;
    private readonly ConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public ConfirmedRabbitPublisher(IConfiguration configuration, ILogger<ConfirmedRabbitPublisher> logger)
    {
        _logger = logger;
        _factory = new ConnectionFactory
        {
            Uri = new Uri(RabbitMqUrl.Build(configuration)),
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            AutomaticRecoveryEnabled = true,
            ClientProvidedName = "rnb-push-publisher",
        };
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true }) return _channel;
        await _gate.WaitAsync(ct);
        try
        {
            if (_channel is { IsOpen: true }) return _channel;
            if (_connection is not { IsOpen: true })
            {
                if (_connection != null) await _connection.DisposeAsync();
                _connection = await _factory.CreateConnectionAsync(ct);
                _logger.LogInformation("RabbitMQ confirmed-publisher connection established");
            }
            var channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
            await RabbitTopology.DeclareAsync(channel, ct);
            _channel = channel;
            return channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<ConfirmedMessage> messages, CancellationToken ct = default)
    {
        var results = new PublishResult[messages.Count];
        if (messages.Count == 0) return results;

        IChannel channel;
        try
        {
            channel = await GetChannelAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RabbitMQ confirmed publisher unavailable");
            Array.Fill(results, new PublishResult(false, OutboxRelayLogic.UnavailablePrefix + Trim(ex.Message)));
            return results;
        }

        // Publishes are started together so the confirms overlap; each task settles with its own ack, nack or return.
        var tasks = new Task[messages.Count];
        for (var i = 0; i < messages.Count; i++)
        {
            var index = i;
            tasks[i] = PublishOneAsync(channel, messages[i], ct).ContinueWith(t =>
                results[index] = t.IsCompletedSuccessfully ? new PublishResult(true) : Failed(t.Exception?.GetBaseException()),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        await Task.WhenAll(tasks);
        return results;
    }

    // Channel or connection trouble (not a nack or a return of this one message): the relay must not count an attempt for it.
    public static bool IsBrokerUnavailable(Exception? ex) => ex is AlreadyClosedException or OperationInterruptedException or BrokerUnreachableException
        or System.IO.IOException or System.Net.Sockets.SocketException or ObjectDisposedException;

    private static PublishResult Failed(Exception? ex)
        => new(false, (IsBrokerUnavailable(ex) ? OutboxRelayLogic.UnavailablePrefix : string.Empty) + Trim(ex?.Message));

    private static async Task PublishOneAsync(IChannel channel, ConfirmedMessage m, CancellationToken ct)
    {
        var props = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = m.MessageId,
            Headers = new Dictionary<string, object?> { [RabbitTopology.AttemptHeader] = m.Attempt },
        };
        await channel.BasicPublishAsync(m.Exchange, m.RoutingKey, mandatory: true, basicProperties: props,
            body: Encoding.UTF8.GetBytes(m.Body), cancellationToken: ct);
    }

    private static string Trim(string? message) => string.IsNullOrEmpty(message) ? "publish failed" : message.Length <= 280 ? message : message[..280];

    public async ValueTask DisposeAsync()
    {
        if (_channel != null) await _channel.DisposeAsync();
        if (_connection != null) await _connection.DisposeAsync();
        _gate.Dispose();
    }
}

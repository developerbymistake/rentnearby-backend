using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace RentNearBy.Infrastructure.Services;

internal interface IRabbitPublishChannel : IAsyncDisposable
{
    bool IsOpen { get; }
    Task DeclareQueueAsync(string queue, IDictionary<string, object?>? arguments, CancellationToken ct);
    Task PublishConfirmedAsync(string queue, ReadOnlyMemory<byte> body, string messageId, CancellationToken ct);
}

internal interface IRabbitChannelFactory : IAsyncDisposable
{
    Task<IRabbitPublishChannel> CreateChannelAsync(CancellationToken ct);
}

internal sealed class RabbitPublishNotConfirmedException(string queue, Exception? inner = null)
    : Exception($"Broker did not confirm the publish to '{queue}'", inner);

// One shared channel. The lock covers channel setup, queue declare (once per channel, null arguments) and the
// start of each publish; the broker confirm is awaited outside it, so many publishes can be in flight at once.
internal sealed class RabbitChannelGate(IRabbitChannelFactory factory) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
    private IRabbitPublishChannel? _channel;

    public async Task PublishAsync(string queue, ReadOnlyMemory<byte> body, string messageId, CancellationToken ct)
    {
        IRabbitPublishChannel channel;
        Task confirm;
        await _lock.WaitAsync(ct);
        try
        {
            channel = await EnsureChannelAsync(ct);
            try
            {
                if (!_declared.Contains(queue))
                {
                    await channel.DeclareQueueAsync(queue, arguments: null, ct);
                    _declared.Add(queue);
                }
                confirm = channel.PublishConfirmedAsync(queue, body, messageId, ct);
            }
            catch
            {
                await DropChannelAsync();
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }

        try
        {
            await confirm;
        }
        catch (Exception ex)
        {
            if (ex is not RabbitPublishNotConfirmedException || !channel.IsOpen)
                await DropIfCurrentAsync(channel);
            throw;
        }
    }

    private async Task DropIfCurrentAsync(IRabbitPublishChannel channel)
    {
        await _lock.WaitAsync();
        try
        {
            if (ReferenceEquals(_channel, channel)) await DropChannelAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IRabbitPublishChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true }) return _channel;
        await DropChannelAsync();
        _channel = await factory.CreateChannelAsync(ct);
        return _channel;
    }

    private async Task DropChannelAsync()
    {
        _declared.Clear();
        var old = _channel;
        _channel = null;
        if (old == null) return;
        try
        {
            await old.DisposeAsync();
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DropChannelAsync();
        await factory.DisposeAsync();
        _lock.Dispose();
    }
}

internal sealed class RabbitConnectionChannelFactory(ConnectionFactory connectionFactory, ILogger logger) : IRabbitChannelFactory
{
    private IConnection? _connection;

    public async Task<IRabbitPublishChannel> CreateChannelAsync(CancellationToken ct)
    {
        if (_connection is not { IsOpen: true })
        {
            if (_connection != null) await _connection.DisposeAsync();
            _connection = null;
            _connection = await connectionFactory.CreateConnectionAsync(ct);
            logger.LogInformation("RabbitMQ publisher connection established");
        }
        var channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        return new RabbitPublishChannel(channel);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null) await _connection.DisposeAsync();
    }

    private sealed class RabbitPublishChannel(IChannel channel) : IRabbitPublishChannel
    {
        public bool IsOpen => channel.IsOpen;

        public Task DeclareQueueAsync(string queue, IDictionary<string, object?>? arguments, CancellationToken ct)
            => channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                arguments: arguments, cancellationToken: ct);

        public async Task PublishConfirmedAsync(string queue, ReadOnlyMemory<byte> body, string messageId, CancellationToken ct)
        {
            var props = new BasicProperties { Persistent = true, MessageId = messageId };
            try
            {
                await channel.BasicPublishAsync(string.Empty, queue, mandatory: false, basicProperties: props,
                    body: body, cancellationToken: ct);
            }
            catch (PublishException ex)
            {
                throw new RabbitPublishNotConfirmedException(queue, ex);
            }
        }

        public ValueTask DisposeAsync() => channel.DisposeAsync();
    }
}

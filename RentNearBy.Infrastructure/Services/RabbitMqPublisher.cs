using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class RabbitMqPublisher : IRabbitMqPublisher, IAsyncDisposable
{
    private readonly ILogger<RabbitMqPublisher> _logger;
    private IConnection? _connection;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConnectionFactory _factory;

    public RabbitMqPublisher(IConfiguration configuration, ILogger<RabbitMqPublisher> logger)
    {
        _logger = logger;
        _factory = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    private async Task<IConnection> GetConnectionAsync()
    {
        if (_connection is { IsOpen: true })
            return _connection;

        await _connectLock.WaitAsync();
        try
        {
            if (_connection is { IsOpen: true })
                return _connection;

            _connection = await _factory.CreateConnectionAsync();
            _logger.LogInformation("RabbitMQ publisher connection established");
            return _connection;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task PublishAsync(string queue, string message)
    {
        var connection = await GetConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await channel.QueueDeclareAsync(
            queue: queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        var body = Encoding.UTF8.GetBytes(message);
        var props = new BasicProperties { Persistent = true };

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: queue,
            mandatory: false,
            basicProperties: props,
            body: body);

        _logger.LogDebug("Published message to queue '{Queue}'", queue);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
            await _connection.DisposeAsync();
        _connectLock.Dispose();
    }

}

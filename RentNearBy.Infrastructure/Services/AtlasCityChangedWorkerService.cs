using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

// Structural copy of StayJobPostedAdminWorkerService, bound to the Atlas fanout exchange with its own queue.
public class AtlasCityChangedWorkerService : BackgroundService
{
    private const string Exchange = "atlas.city.changed";
    private const string QueueName = "atlas.city.changed.rentnearby";

    private static readonly JsonSerializerOptions MessageJson = new() { PropertyNameCaseInsensitive = true };

    private readonly ITripSpotCache _cache;
    private readonly ITripRoadCache _roadCache;
    private readonly ILogger<AtlasCityChangedWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public AtlasCityChangedWorkerService(
        ITripSpotCache cache,
        ITripRoadCache roadCache,
        IConfiguration configuration,
        ILogger<AtlasCityChangedWorkerService> logger)
    {
        _cache = cache;
        _roadCache = roadCache;
        _logger = logger;

        _factory = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AtlasCityChangedWorkerService starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

                // Must match Atlas's declare (fanout, durable, autoDelete false, args null) or RabbitMQ answers 406.
                await channel.ExchangeDeclareAsync(
                    exchange: Exchange,
                    type: ExchangeType.Fanout,
                    durable: true,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: stoppingToken);

                await channel.QueueDeclareAsync(
                    queue: QueueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: stoppingToken);

                await channel.QueueBindAsync(
                    queue: QueueName,
                    exchange: Exchange,
                    routingKey: "",
                    arguments: null,
                    cancellationToken: stoppingToken);

                await channel.BasicQosAsync(
                    prefetchSize: 0,
                    prefetchCount: 10,
                    global: false,
                    cancellationToken: stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    try
                    {
                        var body = Encoding.UTF8.GetString(ea.Body.ToArray());
                        var msg = JsonSerializer.Deserialize<AtlasCityChangedMessage>(body, MessageJson);

                        if (msg != null && msg.CityKey > 0)
                            await InvalidateCityAsync(msg.CityKey);
                        else
                            _logger.LogWarning("AtlasCityChangedWorkerService: dropped a message without a city key");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "AtlasCityChangedWorkerService: error processing message");
                    }
                    finally
                    {
                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                    }
                };

                await channel.BasicConsumeAsync(
                    queue: QueueName,
                    autoAck: false,
                    consumer: consumer,
                    cancellationToken: stoppingToken);

                _logger.LogInformation("AtlasCityChangedWorkerService consuming queue '{Queue}' bound to '{Exchange}'", QueueName, Exchange);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AtlasCityChangedWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("AtlasCityChangedWorkerService stopped");
    }

    internal async Task InvalidateCityAsync(long cityKey)
    {
        await _cache.InvalidateAsync(cityKey);
        await _roadCache.InvalidateAsync(cityKey);
    }
}

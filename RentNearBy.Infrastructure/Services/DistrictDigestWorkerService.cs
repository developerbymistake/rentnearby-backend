using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class DistrictDigestWorkerService : BackgroundService
{
    private const string QueueName = "district.digest.ready";

    private readonly IFcmService _fcmService;
    private readonly ILogger<DistrictDigestWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public DistrictDigestWorkerService(
        IFcmService fcmService,
        IConfiguration configuration,
        ILogger<DistrictDigestWorkerService> logger)
    {
        _fcmService = fcmService;
        _logger     = logger;
        _factory    = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DistrictDigestWorkerService starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _factory.CreateConnectionAsync(stoppingToken);
                await using var channel   = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

                await channel.QueueDeclareAsync(
                    queue:      QueueName,
                    durable:    true,
                    exclusive:  false,
                    autoDelete: false,
                    arguments:  null,
                    cancellationToken: stoppingToken);

                await channel.BasicQosAsync(
                    prefetchSize:  0,
                    prefetchCount: 10,
                    global:        false,
                    cancellationToken: stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    // No DLQ for this queue — a missed digest is best-effort/informational
                    // (unlike membership expiry), so failures are logged inside
                    // ProcessMessageAsync and this handler always ACKs.
                    try
                    {
                        var body = Encoding.UTF8.GetString(ea.Body.ToArray());
                        var msg  = JsonSerializer.Deserialize<DistrictDigestMessage>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (msg != null)
                            await ProcessMessageAsync(msg);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "DistrictDigestWorker: error processing message");
                    }
                    finally
                    {
                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                    }
                };

                await channel.BasicConsumeAsync(
                    queue:    QueueName,
                    autoAck:  false,
                    consumer: consumer,
                    cancellationToken: stoppingToken);

                _logger.LogInformation("DistrictDigestWorkerService consuming queue '{Queue}'", QueueName);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DistrictDigestWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("DistrictDigestWorkerService stopped");
    }

    private async Task ProcessMessageAsync(DistrictDigestMessage msg)
    {
        var topic = $"district_{msg.DistrictId}";
        const string title = "New listings near you";
        var body = BuildBody(msg);

        try
        {
            await _fcmService.SendToTopicAsync(topic, title, body, new Dictionary<string, string>
            {
                { "type", "district_digest" },
                { "district_id", msg.DistrictId.ToString() }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send district digest push for topic {Topic}", topic);
        }
    }

    private static string BuildBody(DistrictDigestMessage msg)
    {
        var parts = new List<string>();
        if (msg.RoomCount > 0) parts.Add($"{msg.RoomCount} new room{(msg.RoomCount == 1 ? "" : "s")}");
        if (msg.PlotCount > 0) parts.Add($"{msg.PlotCount} new plot{(msg.PlotCount == 1 ? "" : "s")}");
        return $"{string.Join(" and ", parts)} added in {msg.DistrictName}";
    }
}

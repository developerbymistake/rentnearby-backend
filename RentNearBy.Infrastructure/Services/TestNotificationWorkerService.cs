using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

/// <summary>
/// TESTING ONLY — Remove this file and its registration in ServiceCollectionExtensions.cs before production.
/// </summary>
public class TestNotificationWorkerService : BackgroundService
{
    private const string QueueName = "test.broadcast";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IFcmService _fcmService;
    private readonly ILogger<TestNotificationWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public TestNotificationWorkerService(
        IServiceScopeFactory serviceScopeFactory,
        IFcmService fcmService,
        IConfiguration configuration,
        ILogger<TestNotificationWorkerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _fcmService = fcmService;
        _logger = logger;

        _factory = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TestNotificationWorkerService starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

                await channel.QueueDeclareAsync(
                    queue: QueueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: stoppingToken);

                await channel.BasicQosAsync(
                    prefetchSize: 0,
                    prefetchCount: 1,
                    global: false,
                    cancellationToken: stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    try
                    {
                        var body = Encoding.UTF8.GetString(ea.Body.ToArray());
                        var msg = JsonSerializer.Deserialize<TestBroadcastMessage>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (msg != null)
                            await ProcessMessageAsync(msg);

                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing test broadcast — nacking");
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                    }
                };

                await channel.BasicConsumeAsync(
                    queue: QueueName,
                    autoAck: false,
                    consumer: consumer,
                    cancellationToken: stoppingToken);

                _logger.LogInformation("TestNotificationWorkerService consuming queue '{Queue}'", QueueName);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TestNotificationWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("TestNotificationWorkerService stopped");
    }

    private async Task ProcessMessageAsync(TestBroadcastMessage msg)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tokens = await db.DeviceTokens
            .Where(d => d.IsValid)
            .ToListAsync();

        if (tokens.Count == 0)
        {
            _logger.LogInformation("TestBroadcast: No active device tokens found");
            return;
        }

        var sent = 0;
        var failed = 0;

        foreach (var deviceToken in tokens)
        {
            try
            {
                var success = await _fcmService.SendAsync(deviceToken.Token, msg.Title, msg.Body, "test");
                if (success) sent++;
                else failed++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "TestBroadcast: FCM send failed for token {Token}",
                    deviceToken.Token[..Math.Min(20, deviceToken.Token.Length)]);
            }
        }

        _logger.LogInformation(
            "TestBroadcast complete — title='{Title}', sent={Sent}, failed={Failed}",
            msg.Title, sent, failed);
    }
}

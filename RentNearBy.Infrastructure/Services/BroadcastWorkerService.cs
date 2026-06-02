using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class BroadcastWorkerService : BackgroundService
{
    private const string QueueName = "broadcast.notification";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IFcmService _fcmService;
    private readonly ILogger<BroadcastWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public BroadcastWorkerService(
        IServiceScopeFactory serviceScopeFactory,
        IFcmService fcmService,
        IConfiguration configuration,
        ILogger<BroadcastWorkerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _fcmService          = fcmService;
        _logger              = logger;
        _factory             = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BroadcastWorkerService starting");

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
                    try
                    {
                        var body = Encoding.UTF8.GetString(ea.Body.ToArray());
                        var msg  = JsonSerializer.Deserialize<BroadcastMessage>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (msg != null)
                            await ProcessBatchAsync(msg);

                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "BroadcastWorker: error processing message — nacking");
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                    }
                };

                await channel.BasicConsumeAsync(
                    queue:    QueueName,
                    autoAck:  false,
                    consumer: consumer,
                    cancellationToken: stoppingToken);

                _logger.LogInformation("BroadcastWorkerService consuming queue '{Queue}'", QueueName);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BroadcastWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("BroadcastWorkerService stopped");
    }

    private async Task ProcessBatchAsync(BroadcastMessage msg)
    {
        using var scope      = _serviceScopeFactory.CreateScope();
        var unitOfWork       = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var userIds = await unitOfWork.DeviceTokens.GetValidTokenUserIdsPagedAsync(msg.Offset, msg.Limit);

        if (userIds.Count == 0)
        {
            _logger.LogInformation("BroadcastWorker: no users in batch offset={Offset}", msg.Offset);
            return;
        }

        var sent   = 0;
        var failed = 0;

        foreach (var userId in userIds)
        {
            var tokens = (await unitOfWork.DeviceTokens.GetValidByUserIdAsync(userId)).ToList();
            foreach (var deviceToken in tokens)
            {
                try
                {
                    var success = await _fcmService.SendAsync(deviceToken.Token, msg.Title, msg.Body, "broadcast");
                    if (success)
                    {
                        sent++;
                    }
                    else
                    {
                        failed++;
                        await unitOfWork.DeviceTokens.MarkInvalidAsync(deviceToken.Token);
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "BroadcastWorker: FCM send failed for user {UserId}", userId);
                }
            }
        }

        await unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "BroadcastWorker: batch offset={Offset} done — users={Users}, sent={Sent}, failed={Failed}",
            msg.Offset, userIds.Count, sent, failed);
    }
}

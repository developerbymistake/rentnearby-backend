using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

public class NotificationWorkerService : BackgroundService
{
    private const string QueueName    = "membership.expired";
    private const string DlqQueueName = "dlq.membership.expired";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IFcmService _fcmService;
    private readonly ILogger<NotificationWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public NotificationWorkerService(
        IServiceScopeFactory serviceScopeFactory,
        IFcmService fcmService,
        IConfiguration configuration,
        ILogger<NotificationWorkerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _fcmService = fcmService;
        _logger = logger;

        _factory = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NotificationWorkerService starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

                // Declare DLQ queue first so it exists before main queue references it
                await channel.QueueDeclareAsync(
                    queue: DlqQueueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: stoppingToken);

                // Main queue: failed messages (NACK) auto-route to DLQ
                // NOTE: If this queue already exists without dead-letter args,
                // delete it from RabbitMQ management console first (one-time setup)
                await channel.QueueDeclareAsync(
                    queue: QueueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: new Dictionary<string, object?>
                    {
                        ["x-dead-letter-exchange"]    = "",
                        ["x-dead-letter-routing-key"] = DlqQueueName,
                    },
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
                        var msg = JsonSerializer.Deserialize<MembershipExpiredMessage>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (msg != null)
                            await ProcessMessageAsync(msg);

                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing notification message — nacking");
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                    }
                };

                await channel.BasicConsumeAsync(
                    queue: QueueName,
                    autoAck: false,
                    consumer: consumer,
                    cancellationToken: stoppingToken);

                _logger.LogInformation("NotificationWorkerService consuming queue '{Queue}'", QueueName);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NotificationWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("NotificationWorkerService stopped");
    }

    private async Task ProcessMessageAsync(MembershipExpiredMessage msg)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var notificationType = msg.Type == "room" ? "room_expired" : "plot_expired";
        var title = "Membership Expired";
        var body = msg.Type == "room"
            ? "Your room listing membership has expired. Renew to keep your listings active."
            : "Your plot listing membership has expired. Renew to keep your listings active.";

        if (await unitOfWork.NotificationLogs.WasSentTodayAsync(msg.UserId, notificationType))
        {
            _logger.LogInformation(
                "Notification '{Type}' already sent today for user {UserId}, skipping",
                notificationType, msg.UserId);
            return;
        }

        var tokens = (await unitOfWork.DeviceTokens.GetValidByUserIdAsync(msg.UserId)).ToList();

        if (tokens.Count == 0)
        {
            _logger.LogInformation("No valid device tokens for user {UserId}", msg.UserId);
            return;
        }

        foreach (var deviceToken in tokens)
        {
            string? errorMessage = null;
            var isSuccess = false;

            try
            {
                isSuccess = await _fcmService.SendAsync(deviceToken.Token, title, body, msg.Type);

                if (!isSuccess)
                {
                    await unitOfWork.DeviceTokens.MarkInvalidAsync(deviceToken.Token);
                    errorMessage = "RegistrationTokenNotRegistered";
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                _logger.LogError(ex, "FCM send exception for user {UserId}", msg.UserId);
            }

            await unitOfWork.NotificationLogs.AddAsync(new NotificationLog
            {
                Id = Guid.NewGuid(),
                UserId = msg.UserId,
                Type = notificationType,
                IsSuccess = isSuccess,
                ErrorMessage = errorMessage,
                SentAt = DateTime.UtcNow
            });
        }

        await unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "Notification '{Type}' processed for user {UserId}, tokens={TokenCount}",
            notificationType, msg.UserId, tokens.Count);
    }
}

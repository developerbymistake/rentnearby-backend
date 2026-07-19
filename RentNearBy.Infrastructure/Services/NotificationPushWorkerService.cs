using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

// Fully separate from InquiryStatusPushWorkerService — own queue, no shared code — deliberately, so
// the existing Inquiry-status push to a submitting consumer is never touched by this feature. Unlike
// that worker, this one formats nothing itself: Title/Body/ActionRoute/ActionArgumentsJson are
// already persisted on the NotificationEvent row by whichever handler wrote it, so every current and
// future producer gets FCM delivery for free through this one worker, with no per-category
// formatting logic living here — with one deliberate exception: LeadAssigned rows also fan out to
// every Admin device (see ProcessMessageAsync), so Admin stays aware of every lead an Agent gets,
// without a separate queue/worker/storage. Mirrors ReportFiledWorkerService's existing
// AdminDeviceTokens broadcast loop exactly.
public class NotificationPushWorkerService : BackgroundService
{
    private const string QueueName = "notification.push";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IFcmService _fcmService;
    private readonly ILogger<NotificationPushWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public NotificationPushWorkerService(
        IServiceScopeFactory serviceScopeFactory,
        IFcmService fcmService,
        IConfiguration configuration,
        ILogger<NotificationPushWorkerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _fcmService = fcmService;
        _logger = logger;

        _factory = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NotificationPushWorkerService starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(
                        publisherConfirmationsEnabled: false,
                        publisherConfirmationTrackingEnabled: false,
                        outstandingPublisherConfirmationsRateLimiter: null,
                        consumerDispatchConcurrency: 4),
                    cancellationToken: stoppingToken);

                await channel.QueueDeclareAsync(
                    queue: QueueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
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
                        var msg = JsonSerializer.Deserialize<NotificationPushPayload>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (msg != null)
                            await ProcessMessageAsync(msg);

                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing notification push message — nacking");
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                    }
                };

                await channel.BasicConsumeAsync(
                    queue: QueueName,
                    autoAck: false,
                    consumer: consumer,
                    cancellationToken: stoppingToken);

                _logger.LogInformation("NotificationPushWorkerService consuming queue '{Queue}'", QueueName);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NotificationPushWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("NotificationPushWorkerService stopped");
    }

    private async Task ProcessMessageAsync(NotificationPushPayload msg)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var notification = await unitOfWork.Notifications.GetByIdAsync(msg.NotificationId);
        if (notification == null)
        {
            _logger.LogInformation("NotificationEvent {Id} no longer exists, skipping push", msg.NotificationId);
            return;
        }

        var tokens = (await unitOfWork.DeviceTokens.GetValidByUserIdAsync(msg.UserId)).ToList();
        if (tokens.Count == 0)
        {
            _logger.LogInformation("No valid device tokens for user {UserId}", msg.UserId);
            return;
        }

        var data = new Dictionary<string, string> { { "notification_type", NotificationTypes.ToWireValue(notification.Type) } };
        if (notification.ActionRoute != null) data["action_route"] = notification.ActionRoute;
        if (notification.ActionArgumentsJson != null) data["action_args_json"] = notification.ActionArgumentsJson;

        // Sends are independent, so they run in parallel (same reasoning as
        // InquiryStatusPushWorkerService) — invalid-token cleanup collected here, applied
        // sequentially below since the DbContext behind unitOfWork isn't safe for concurrent writes.
        var sendResults = await Task.WhenAll(tokens.Select(async deviceToken =>
        {
            try
            {
                var isSuccess = await _fcmService.SendAsync(deviceToken.Token, notification.Title, notification.Body,
                    NotificationTypes.ToWireValue(notification.Type), data);
                return (deviceToken.Token, isSuccess);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification FCM send exception for user {UserId}", msg.UserId);
                return (deviceToken.Token, isSuccess: true); // transient error, not proof the token itself is invalid
            }
        }));

        foreach (var (token, isSuccess) in sendResults)
            if (!isSuccess)
                await unitOfWork.DeviceTokens.MarkInvalidAsync(token);

        // Admin awareness: every LeadAssigned row also pushes to every Admin device, reusing the same
        // Title/Body/data already built above (no separate admin-phrased copy, no new storage) — a
        // multi-agent inquiry creates one NotificationEvent per Agent, so Admin gets one push per
        // Agent notified, matching the admin feed showing one row per Agent too.
        if (notification.Type == NotificationTypes.LeadAssigned)
        {
            var adminTokens = (await unitOfWork.AdminDeviceTokens.GetAllValidAsync()).ToList();
            foreach (var adminToken in adminTokens)
            {
                try
                {
                    var isSuccess = await _fcmService.SendAsync(adminToken.Token, notification.Title, notification.Body,
                        NotificationTypes.ToWireValue(notification.Type), data);
                    if (!isSuccess) await unitOfWork.AdminDeviceTokens.MarkInvalidAsync(adminToken.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Notification FCM send exception for admin token");
                }
            }
        }

        await unitOfWork.SaveChangesAsync();
    }
}

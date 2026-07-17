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

/// <summary>
/// Consumes the dead-letter queue "dlq.listing.expired".
/// Messages arrive here when:
///   1. NotificationWorkerService NACKs a message (processing failed)
///   2. ListingExpirySweepService fails to publish to main queue (publishes directly to DLQ)
///
/// Strategy: retry once, then ACK regardless — avoids infinite loop.
/// </summary>
public class DlqNotificationWorkerService : BackgroundService
{
    private const string QueueName = "dlq.listing.expired";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IFcmService _fcmService;
    private readonly ILogger<DlqNotificationWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public DlqNotificationWorkerService(
        IServiceScopeFactory serviceScopeFactory,
        IFcmService fcmService,
        IConfiguration configuration,
        ILogger<DlqNotificationWorkerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _fcmService          = fcmService;
        _logger              = logger;
        _factory             = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DlqNotificationWorkerService starting");

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
                    prefetchCount: 5,
                    global:        false,
                    cancellationToken: stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    // Always ACK — even on failure — to avoid infinite DLQ loop
                    try
                    {
                        var body = Encoding.UTF8.GetString(ea.Body.ToArray());
                        var msg  = JsonSerializer.Deserialize<ListingExpiredMessage>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (msg != null)
                            await ProcessDlqMessageAsync(msg);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "DLQ: unhandled error — ACKing to prevent infinite loop");
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

                _logger.LogInformation("DlqNotificationWorkerService consuming queue '{Queue}'", QueueName);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DlqNotificationWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("DlqNotificationWorkerService stopped");
    }

    private async Task ProcessDlqMessageAsync(ListingExpiredMessage msg)
    {
        _logger.LogInformation("DLQ: retrying notification for user {UserId} kind={ListingKind}", msg.UserId, msg.ListingKind);

        using var scope    = _serviceScopeFactory.CreateScope();
        var unitOfWork     = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var isRoom         = msg.ListingKind == ListingKinds.Room;
        var notifType      = isRoom ? "room_expired" : "plot_expired";
        var title          = "Listing Expired";
        var body           = isRoom
            ? "Your room listing has gone offline — its paid period ended. Go live again to keep it visible."
            : "Your plot listing has gone offline — its paid period ended. Go live again to keep it visible.";

        // Skip if already sent today (might have succeeded via main worker earlier)
        if (await unitOfWork.NotificationLogs.WasSentTodayAsync(msg.UserId, notifType))
        {
            _logger.LogInformation("DLQ: notification already sent today for user {UserId}, skipping", msg.UserId);
            return;
        }

        var tokens = (await unitOfWork.DeviceTokens.GetValidByUserIdAsync(msg.UserId)).ToList();
        if (tokens.Count == 0)
        {
            _logger.LogInformation("DLQ: no valid tokens for user {UserId}", msg.UserId);
            return;
        }

        foreach (var deviceToken in tokens)
        {
            string? errorMessage = null;
            var isSuccess = false;

            try
            {
                isSuccess = await _fcmService.SendAsync(deviceToken.Token, title, body, msg.ListingKind.ToLowerInvariant());
                if (!isSuccess)
                {
                    await unitOfWork.DeviceTokens.MarkInvalidAsync(deviceToken.Token);
                    errorMessage = "RegistrationTokenNotRegistered";
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                _logger.LogError(ex, "DLQ: FCM send failed for user {UserId}", msg.UserId);
            }

            await unitOfWork.NotificationLogs.AddAsync(new Core.Entities.NotificationLog
            {
                Id           = Guid.NewGuid(),
                UserId       = msg.UserId,
                Type         = notifType,
                IsSuccess    = isSuccess,
                ErrorMessage = errorMessage,
                SentAt       = DateTime.UtcNow,
            });
        }

        await unitOfWork.SaveChangesAsync();
        _logger.LogInformation("DLQ: retry complete for user {UserId}", msg.UserId);
    }
}

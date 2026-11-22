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
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

public sealed record OldPushNotification(Guid Id, string Type, string Title, string Body, string? ActionRoute, string? ActionArgumentsJson);

public interface IOldPushStore
{
    Task<OldPushNotification?> GetNotificationAsync(Guid notificationId, CancellationToken ct);
    Task<IReadOnlyList<string>> GetTokensAsync(Guid userId, CancellationToken ct);
    Task MarkInvalidAsync(IReadOnlyCollection<string> tokens, CancellationToken ct);
}

public sealed class OldPushStore(IServiceScopeFactory scopeFactory) : IOldPushStore
{
    public async Task<OldPushNotification?> GetNotificationAsync(Guid notificationId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var n = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().Notifications.GetByIdAsync(notificationId);
        return n == null ? null : new OldPushNotification(n.Id, n.Type, n.Title, n.Body, n.ActionRoute, n.ActionArgumentsJson);
    }

    public async Task<IReadOnlyList<string>> GetTokensAsync(Guid userId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tokens = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DeviceTokens.GetValidByUserIdAsync(userId);
        return tokens.Select(t => t.Token).Distinct().ToList();
    }

    public async Task MarkInvalidAsync(IReadOnlyCollection<string> tokens, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DeviceTokens.MarkInvalidManyAsync(tokens, ct);
    }
}

public enum OldPushOutcome { Delivered, NoLiveToken, HandOver }

public enum OldPushAck { Ack, Requeue }

public static class OldPushVerdict
{
    // Delivered when any token took it; nothing left to try when every token is dead; any transient failure (or exception) hands it over.
    public static OldPushOutcome Decide(IReadOnlyList<FcmSendOutcome> tokenResults)
    {
        if (tokenResults.Any(r => r == FcmSendOutcome.Sent)) return OldPushOutcome.Delivered;
        return tokenResults.Any(r => r == FcmSendOutcome.Failed) ? OldPushOutcome.HandOver : OldPushOutcome.NoLiveToken;
    }

    public static string HandOverKey(Guid notificationId) => $"np:{notificationId:N}";

    public static OutboxPushPayload HandOverPayload(Guid userId, OldPushNotification n)
    {
        var key = HandOverKey(n.Id);
        return new OutboxPushPayload(key, userId, n.Id, n.Title, n.Body, n.ActionRoute, n.ActionArgumentsJson, key);
    }
}

// One notification.push message: an exception is never success. A transient failure is handed to the rnb.push pipeline
// (which has the retry ladder); if that hand-over is not confirmed the message is requeued after a pause.
public sealed class OldPushHandler(IOldPushStore store, IFcmService fcm, IConfirmedPublisher publisher, ILogger logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<OldPushAck> HandleAsync(NotificationPushPayload msg, CancellationToken ct)
    {
        OldPushNotification? notification = null;
        try
        {
            notification = await store.GetNotificationAsync(msg.NotificationId, ct);
            if (notification == null)
            {
                logger.LogInformation("NotificationEvent {Id} no longer exists, skipping push", msg.NotificationId);
                return OldPushAck.Ack;
            }

            var tokens = msg.TargetTokens is { Count: > 0 } snapshot ? snapshot : await store.GetTokensAsync(msg.UserId, ct);
            if (tokens.Count == 0)
            {
                logger.LogInformation("No valid device tokens for user {UserId}", msg.UserId);
                return OldPushAck.Ack;
            }

            var wireType = NotificationTypes.ToWireValue(notification.Type);
            var data = new Dictionary<string, string> { { "notification_type", wireType } };
            if (notification.ActionRoute != null) data["action_route"] = notification.ActionRoute;
            if (notification.ActionArgumentsJson != null) data["action_args_json"] = notification.ActionArgumentsJson;

            var results = await Task.WhenAll(tokens.Select(async token =>
            {
                try
                {
                    var ok = await fcm.SendAsync(token, notification.Title, notification.Body, wireType, data);
                    return (token, outcome: ok ? FcmSendOutcome.Sent : FcmSendOutcome.InvalidToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Notification FCM send failed for user {UserId}", msg.UserId);
                    return (token, outcome: FcmSendOutcome.Failed);
                }
            }));

            var dead = results.Where(r => r.outcome == FcmSendOutcome.InvalidToken).Select(r => r.token).ToList();
            if (dead.Count > 0)
            {
                try { await store.MarkInvalidAsync(dead, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Could not mark {Count} dead tokens", dead.Count); }
            }

            var outcome = OldPushVerdict.Decide(results.Select(r => r.outcome).ToList());
            if (outcome != OldPushOutcome.HandOver) return OldPushAck.Ack;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Notification push {Id} failed", msg.NotificationId);
            if (notification == null) return OldPushAck.Requeue;
        }

        return await HandOverAsync(msg.UserId, notification, ct);
    }

    private async Task<OldPushAck> HandOverAsync(Guid userId, OldPushNotification notification, CancellationToken ct)
    {
        var payload = OldPushVerdict.HandOverPayload(userId, notification);
        var message = new ConfirmedMessage(RabbitTopology.Exchange, RabbitTopology.SendKey, JsonSerializer.Serialize(payload, Json), payload.Key);
        try
        {
            var results = await publisher.PublishBatchAsync([message], ct);
            if (results.Count == 1 && results[0].Confirmed) return OldPushAck.Ack;
            logger.LogWarning("Hand-over of notification push {Id} not confirmed: {Error}", notification.Id, results.Count == 1 ? results[0].Error : "no result");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Hand-over of notification push {Id} failed", notification.Id);
        }
        return OldPushAck.Requeue;
    }
}

// Formats nothing itself: Title/Body/ActionRoute/ActionArgumentsJson are already persisted on the
// NotificationEvent row by whichever handler wrote it, so every producer gets FCM delivery through this one worker.
public class NotificationPushWorkerService : BackgroundService
{
    private const string QueueName = "notification.push";
    public const int RequeueDelayMs = 5000;

    private readonly OldPushHandler _handler;
    private readonly ILogger<NotificationPushWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public NotificationPushWorkerService(
        IServiceScopeFactory serviceScopeFactory,
        IFcmService fcmService,
        IConfirmedPublisher publisher,
        IConfiguration configuration,
        ILogger<NotificationPushWorkerService> logger)
    {
        _logger = logger;
        _handler = new OldPushHandler(new OldPushStore(serviceScopeFactory), fcmService, publisher, logger);
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
                    NotificationPushPayload? msg;
                    try
                    {
                        msg = JsonSerializer.Deserialize<NotificationPushPayload>(Encoding.UTF8.GetString(ea.Body.Span),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, "Unreadable notification push message, dropping");
                        msg = null;
                    }

                    try
                    {
                        var ack = msg == null ? OldPushAck.Ack : await _handler.HandleAsync(msg, stoppingToken);
                        if (ack == OldPushAck.Ack)
                        {
                            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                            return;
                        }
                        await Task.Delay(RequeueDelayMs, stoppingToken);
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Could not settle notification push message {Tag}", ea.DeliveryTag);
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
                try { await Task.Delay(5000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("NotificationPushWorkerService stopped");
    }
}

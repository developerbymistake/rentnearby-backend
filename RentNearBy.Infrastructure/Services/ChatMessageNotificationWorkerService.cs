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

// Independent of NotificationWorkerService — new file, no shared code, mirrors its
// shape only (own queue/DLQ, own BackgroundService). Uses the generic DeviceTokens
// repo directly (safe, generic data access) but deliberately does NOT call
// NotificationLogs — that dedup is membership-expiry-specific ("once per day"),
// the wrong semantics for a chat message that can legitimately arrive many times a day.
public class ChatMessageNotificationWorkerService : BackgroundService
{
    private const string QueueName    = "chat.message.push";
    private const string DlqQueueName = "dlq.chat.message.push";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IChatFcmService _chatFcmService;
    private readonly ILogger<ChatMessageNotificationWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public ChatMessageNotificationWorkerService(
        IServiceScopeFactory serviceScopeFactory,
        IChatFcmService chatFcmService,
        IConfiguration configuration,
        ILogger<ChatMessageNotificationWorkerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _chatFcmService = chatFcmService;
        _logger = logger;

        _factory = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ChatMessageNotificationWorkerService starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _factory.CreateConnectionAsync(stoppingToken);
                // Default dispatch concurrency is 1 (messages processed one at a time even
                // though each one's own work — DB read + parallel FCM sends — is I/O-bound, not
                // CPU-bound). Raising this lets up to 4 messages be in flight together, using
                // otherwise-idle wait time instead of adding real CPU/DB load.
                await using var channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(
                        publisherConfirmationsEnabled: false,
                        publisherConfirmationTrackingEnabled: false,
                        outstandingPublisherConfirmationsRateLimiter: null,
                        consumerDispatchConcurrency: 4),
                    cancellationToken: stoppingToken);

                await channel.QueueDeclareAsync(
                    queue: DlqQueueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: stoppingToken);

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
                        var msg = JsonSerializer.Deserialize<ChatMessagePushPayload>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (msg != null)
                            await ProcessMessageAsync(msg);

                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing chat push message — nacking");
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                    }
                };

                await channel.BasicConsumeAsync(
                    queue: QueueName,
                    autoAck: false,
                    consumer: consumer,
                    cancellationToken: stoppingToken);

                _logger.LogInformation("ChatMessageNotificationWorkerService consuming queue '{Queue}'", QueueName);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChatMessageNotificationWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("ChatMessageNotificationWorkerService stopped");
    }

    private async Task ProcessMessageAsync(ChatMessagePushPayload msg)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var tokens = (await unitOfWork.DeviceTokens.GetValidByUserIdAsync(msg.RecipientUserId)).ToList();
        if (tokens.Count == 0)
        {
            _logger.LogInformation("No valid device tokens for user {UserId}", msg.RecipientUserId);
            return;
        }

        // Sends are independent (no shared DbContext access), so they run in parallel — the
        // previous sequential loop meant a user with several devices waited on N round-trips
        // to FCM one after another. Invalid-token cleanup is collected here and applied
        // sequentially below instead, since the DbContext behind unitOfWork (one scope per
        // message) isn't safe for concurrent writes from multiple tasks.
        var sendResults = await Task.WhenAll(tokens.Select(async deviceToken =>
        {
            try
            {
                var isSuccess = await _chatFcmService.SendAsync(deviceToken.Token, msg.SenderName, msg.Preview, msg.ConversationId);
                return (deviceToken.Token, isSuccess);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chat FCM send exception for user {UserId}", msg.RecipientUserId);
                return (deviceToken.Token, isSuccess: true); // transient error, not proof the token itself is invalid
            }
        }));

        foreach (var (token, isSuccess) in sendResults)
            if (!isSuccess)
                await unitOfWork.DeviceTokens.MarkInvalidAsync(token);

        await unitOfWork.SaveChangesAsync();
    }
}

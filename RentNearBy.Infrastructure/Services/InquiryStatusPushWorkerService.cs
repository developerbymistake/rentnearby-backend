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

// Independent of ChatMessageNotificationWorkerService/NotificationWorkerService — new file, no
// shared code, mirrors ChatMessageNotificationWorkerService's shape only (own queue, own
// IServiceScopeFactory scope per message). Uses the generic IFcmService (a normal rendered
// notification: block, not IChatFcmService's data-only style — there's no per-message-stacking
// need for a status change) and deliberately does NOT call NotificationLogs — that dedup is
// membership-expiry-specific ("once per day"), the wrong semantics for an inquiry status change.
public class InquiryStatusPushWorkerService : BackgroundService
{
    private const string QueueName = "inquiry.status.push";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IFcmService _fcmService;
    private readonly ILogger<InquiryStatusPushWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public InquiryStatusPushWorkerService(
        IServiceScopeFactory serviceScopeFactory,
        IFcmService fcmService,
        IConfiguration configuration,
        ILogger<InquiryStatusPushWorkerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _fcmService = fcmService;
        _logger = logger;

        _factory = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InquiryStatusPushWorkerService starting");

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

                // arguments: null — must match what the shared RabbitMqPublisher.PublishAsync
                // declares with (it has no way to pass custom queue arguments), or RabbitMQ
                // rejects the mismatched redeclare with a 406 and every publish silently fails.
                // Same simple shape as ChatMessageNotificationWorkerService/ReportFiledWorkerService
                // — no DLQ, nothing ever consumed dlq.inquiry.status.push anyway, so a native
                // dead-letter queue here provided no real retry value.
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
                        var msg = JsonSerializer.Deserialize<InquiryStatusPushPayload>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (msg != null)
                            await ProcessMessageAsync(msg);

                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing inquiry status push message — nacking");
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                    }
                };

                await channel.BasicConsumeAsync(
                    queue: QueueName,
                    autoAck: false,
                    consumer: consumer,
                    cancellationToken: stoppingToken);

                _logger.LogInformation("InquiryStatusPushWorkerService consuming queue '{Queue}'", QueueName);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "InquiryStatusPushWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("InquiryStatusPushWorkerService stopped");
    }

    private async Task ProcessMessageAsync(InquiryStatusPushPayload msg)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var tokens = (await unitOfWork.DeviceTokens.GetValidByUserIdAsync(msg.RecipientUserId)).ToList();
        if (tokens.Count == 0)
        {
            _logger.LogInformation("No valid device tokens for user {UserId}", msg.RecipientUserId);
            return;
        }

        // ForAgent recipients are a co-assigned agent hearing about a lead they don't own, not the
        // consumer who submitted it — different wording, and a different notification_type so the
        // client routes the tap to Lead Detail (GET /agents/me/leads/{id}) instead of the
        // consumer-only Inquiry Detail (which 403s for an agent — see InquiryHandlers.GetInquiryDetail's
        // ownership check).
        var title = msg.ForAgent ? "Lead status updated" : "Inquiry update";
        var body = msg.ForAgent
            ? $"Your assigned lead for '{msg.ServiceName}' is now {msg.Status}."
            : $"Your inquiry for '{msg.ServiceName}' is now {msg.Status}.";
        var notificationType = msg.ForAgent ? "agent_lead_status" : "inquiry_status";
        var data = new Dictionary<string, string>
        {
            { "inquiry_id", msg.InquiryId.ToString() },
            { "status", msg.Status },
        };

        // Sends are independent (no shared DbContext access), so they run in parallel — the
        // previous sequential loop meant a user with several devices waited on N round-trips
        // to FCM one after another. Invalid-token cleanup is collected here and applied
        // sequentially below instead, since the DbContext behind unitOfWork (one scope per
        // message) isn't safe for concurrent writes from multiple tasks.
        var sendResults = await Task.WhenAll(tokens.Select(async deviceToken =>
        {
            try
            {
                var isSuccess = await _fcmService.SendAsync(deviceToken.Token, title, body, notificationType, data);
                return (deviceToken.Token, isSuccess);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inquiry status FCM send exception for user {UserId}", msg.RecipientUserId);
                return (deviceToken.Token, isSuccess: true); // transient error, not proof the token itself is invalid
            }
        }));

        foreach (var (token, isSuccess) in sendResults)
            if (!isSuccess)
                await unitOfWork.DeviceTokens.MarkInvalidAsync(token);

        await unitOfWork.SaveChangesAsync();
    }
}

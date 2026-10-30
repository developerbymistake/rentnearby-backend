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
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Structural copy of StayJobPostedAdminWorkerService: admin-only FCM for a granted Play charge or a chargeback review.
public class PlanPurchaseAdminWorkerService : BackgroundService
{
    private const string QueueName = PlanPurchaseAdminPublisher.Queue;
    private const string ChargebackHint = "Answer in Play Console in 24 h.";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IFcmService _fcmService;
    private readonly ILogger<PlanPurchaseAdminWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public PlanPurchaseAdminWorkerService(
        IServiceScopeFactory serviceScopeFactory,
        IFcmService fcmService,
        IConfiguration configuration,
        ILogger<PlanPurchaseAdminWorkerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _fcmService = fcmService;
        _logger = logger;

        _factory = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PlanPurchaseAdminWorkerService starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

                // arguments: null — must match what the shared RabbitMqPublisher.PublishAsync
                // declares with, or RabbitMQ rejects the mismatched redeclare with a 406 and every
                // publish silently fails. No DLQ.
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
                        var msg = JsonSerializer.Deserialize<PlanPurchaseAdminMessage>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (msg != null)
                            await ProcessMessageAsync(msg);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "PlanPurchaseAdminWorkerService: error processing message");
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

                _logger.LogInformation("PlanPurchaseAdminWorkerService consuming queue '{Queue}'", QueueName);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PlanPurchaseAdminWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("PlanPurchaseAdminWorkerService stopped");
    }

    private async Task ProcessMessageAsync(PlanPurchaseAdminMessage msg)
    {
        var isChargeback = msg.Kind == PlanPurchaseAdminKinds.ChargebackReview;
        if (!isChargeback && msg.Kind is not (PlanPurchaseAdminKinds.Granted or PlanPurchaseAdminKinds.Renewed
                or PlanPurchaseAdminKinds.Switched or PlanPurchaseAdminKinds.Recovered))
        {
            _logger.LogInformation("PlanPurchaseAdminWorker: unknown kind {Kind}, skipping push", msg.Kind);
            return;
        }
        if (!isChargeback && msg.PurchaseId == null)
        {
            _logger.LogInformation("PlanPurchaseAdminWorker: {Kind} message without a purchase id, skipping push", msg.Kind);
            return;
        }

        using var scope = _serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        string title;
        string body;
        Guid? userId = null;

        if (msg.PurchaseId is { } purchaseId)
        {
            var purchase = await db.PlanPurchases.AsNoTracking()
                .Where(p => p.Id == purchaseId)
                .Select(p => new
                {
                    p.Status, p.PlanNameSnapshot, p.PayableInr, p.FeatureKey, p.UserId,
                    p.PlayOfferId, p.PlayOrderId, p.PlayChargebackReviewAt, p.GrantedDays,
                })
                .FirstOrDefaultAsync();

            if (purchase == null)
            {
                _logger.LogInformation("PlanPurchaseAdminWorker: purchase {PurchaseId} no longer exists, skipping push", purchaseId);
                return;
            }
            if (!isChargeback && purchase.Status != PurchaseStatuses.Granted)
            {
                _logger.LogInformation("PlanPurchaseAdminWorker: purchase {PurchaseId} is {Status}, skipping push",
                    purchaseId, purchase.Status);
                return;
            }
            if (isChargeback && purchase.PlayChargebackReviewAt == null)
            {
                _logger.LogInformation("PlanPurchaseAdminWorker: purchase {PurchaseId} is not flagged for chargeback review, skipping push",
                    purchaseId);
                return;
            }

            userId = purchase.UserId;
            var user = userId == null ? null : await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.Name, u.PhoneNumber })
                .FirstOrDefaultAsync();
            var name = user?.Name;
            var userLabel = !string.IsNullOrWhiteSpace(name)
                ? name.Trim()
                : PlanPurchaseService.MaskPhone(user?.PhoneNumber) is { Length: > 0 } masked ? masked : "Deleted user";

            var paidLine = $"{purchase.PlanNameSnapshot} · ₹{purchase.PayableInr} · {userLabel}";
            (title, body) = msg.Kind switch
            {
                PlanPurchaseAdminKinds.ChargebackReview => ("Chargeback review", $"{paidLine}. {ChargebackHint}"),
                _ when purchase.PayableInr == 0 => ("Trial started", $"{purchase.PlanNameSnapshot} · {purchase.GrantedDays} days · {userLabel}"),
                PlanPurchaseAdminKinds.Renewed => ("Subscription renewed", paidLine),
                PlanPurchaseAdminKinds.Switched => ("Plan changed", paidLine),
                PlanPurchaseAdminKinds.Recovered => ("Subscription recovered", paidLine),
                _ => ("New subscription", paidLine),
            };
        }
        else
        {
            title = "Chargeback review";
            body = $"Order {msg.PlayOrderId ?? "unknown"}. {ChargebackHint}";
        }

        var wire = NotificationTypes.ToWireValue(isChargeback ? NotificationTypes.PlanChargebackReview : NotificationTypes.PlanPurchased);
        var destination = new NotificationDestination(
            AdminNotificationRoutes.Purchases,
            JsonSerializer.Serialize(new { userId, purchaseId = msg.PurchaseId, kind = msg.Kind }));

        var adminTokens = (await unitOfWork.AdminDeviceTokens.GetAllValidAsync()).ToList();
        foreach (var token in adminTokens)
        {
            try
            {
                var ok = await _fcmService.SendAsync(token.Token, title, body, wire, destination);
                if (!ok) await unitOfWork.AdminDeviceTokens.MarkInvalidAsync(token.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FCM send failed for admin token, purchase {PurchaseId} ({Kind})", msg.PurchaseId, msg.Kind);
            }
        }

        await unitOfWork.SaveChangesAsync();
    }
}

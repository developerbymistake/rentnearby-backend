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

public class ReportFiledWorkerService : BackgroundService
{
    private const string QueueName = "report.filed";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IFcmService _fcmService;
    private readonly ILogger<ReportFiledWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public ReportFiledWorkerService(
        IServiceScopeFactory serviceScopeFactory,
        IFcmService fcmService,
        IConfiguration configuration,
        ILogger<ReportFiledWorkerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _fcmService = fcmService;
        _logger = logger;

        _factory = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReportFiledWorkerService starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

                // No DLQ — a missed report notification is best-effort/informational,
                // the report row is already durably saved regardless of push delivery
                // (same reasoning DistrictDigestWorkerService uses).
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
                        var msg = JsonSerializer.Deserialize<ReportFiledMessage>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (msg != null)
                            await ProcessMessageAsync(msg);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ReportFiledWorkerService: error processing message");
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

                _logger.LogInformation("ReportFiledWorkerService consuming queue '{Queue}'", QueueName);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReportFiledWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("ReportFiledWorkerService stopped");
    }

    private async Task ProcessMessageAsync(ReportFiledMessage msg)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        if (msg.NotifyOwner)
        {
            const string ownerTitle = "Listing flagged for review";
            var ownerBody = $"Your listing '{msg.ListingTitle}' was flagged: {msg.ReasonName}. Please review it to avoid removal.";
            var ownerData = new Dictionary<string, string>
            {
                { "listing_id", msg.ListingId.ToString() },
                { "listing_type", msg.ListingType },
                { "listing_title", msg.ListingTitle },
            };

            var ownerTokens = (await unitOfWork.DeviceTokens.GetValidByUserIdAsync(msg.OwnerId)).ToList();
            if (ownerTokens.Count == 0)
            {
                _logger.LogInformation("No valid device tokens for owner {OwnerId}", msg.OwnerId);
            }
            foreach (var token in ownerTokens)
            {
                try
                {
                    var ok = await _fcmService.SendAsync(token.Token, ownerTitle, ownerBody, "report", ownerData);
                    if (!ok) await unitOfWork.DeviceTokens.MarkInvalidAsync(token.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FCM send failed for owner {OwnerId}", msg.OwnerId);
                }
            }
        }

        // Admins are notified on every report (not just the first pending one) — unlike
        // the owner, there's no per-listing anti-spam concern for moderation staff.
        const string adminTitle = "New listing report";
        var adminBody = $"'{msg.ListingTitle}' was reported: {msg.ReasonName}.";

        var adminTokens = (await unitOfWork.AdminDeviceTokens.GetAllValidAsync()).ToList();
        foreach (var token in adminTokens)
        {
            try
            {
                var ok = await _fcmService.SendAsync(token.Token, adminTitle, adminBody, "report");
                if (!ok) await unitOfWork.AdminDeviceTokens.MarkInvalidAsync(token.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FCM send failed for admin token");
            }
        }

        await unitOfWork.SaveChangesAsync();
    }
}

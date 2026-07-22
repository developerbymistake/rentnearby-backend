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

// Structural copy of EscalationFiledWorkerService — same "publish -> dedicated consumer ->
// AdminDeviceTokens + FCM" pattern, applied to a category-mapped-agent-less Inquiry (CreateInquiry's
// zero-agent branch) instead. No DLQ, same reasoning: the Inquiry row is already durably saved
// regardless of push delivery.
public class InquiryUnassignedWorkerService : BackgroundService
{
    private const string QueueName = "inquiry.unassigned";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IFcmService _fcmService;
    private readonly ILogger<InquiryUnassignedWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public InquiryUnassignedWorkerService(
        IServiceScopeFactory serviceScopeFactory,
        IFcmService fcmService,
        IConfiguration configuration,
        ILogger<InquiryUnassignedWorkerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _fcmService = fcmService;
        _logger = logger;

        _factory = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InquiryUnassignedWorkerService starting");

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
                    prefetchCount: 10,
                    global: false,
                    cancellationToken: stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    try
                    {
                        var body = Encoding.UTF8.GetString(ea.Body.ToArray());
                        var msg = JsonSerializer.Deserialize<InquiryUnassignedMessage>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (msg != null)
                            await ProcessMessageAsync(msg);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "InquiryUnassignedWorkerService: error processing message");
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

                _logger.LogInformation("InquiryUnassignedWorkerService consuming queue '{Queue}'", QueueName);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "InquiryUnassignedWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("InquiryUnassignedWorkerService stopped");
    }

    private async Task ProcessMessageAsync(InquiryUnassignedMessage msg)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        const string adminTitle = "Unassigned inquiry";
        var adminBody = $"{msg.ConsumerName}'s inquiry for '{msg.ServiceName}' has no agent assigned.";

        var adminTokens = (await unitOfWork.AdminDeviceTokens.GetAllValidAsync()).ToList();
        foreach (var token in adminTokens)
        {
            try
            {
                var ok = await _fcmService.SendAsync(token.Token, adminTitle, adminBody, "inquiry_unassigned");
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

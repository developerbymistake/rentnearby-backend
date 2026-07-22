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

// Structural copy of ReportFiledWorkerService — same "publish -> dedicated consumer ->
// AdminDeviceTokens + FCM" pattern this codebase already uses for listing reports, applied to
// consumer escalations (complaints about an assigned agent) instead. No DLQ, same reasoning: the
// InquiryEscalation row is already durably saved regardless of push delivery.
public class EscalationFiledWorkerService : BackgroundService
{
    private const string QueueName = "escalation.filed";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IFcmService _fcmService;
    private readonly ILogger<EscalationFiledWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public EscalationFiledWorkerService(
        IServiceScopeFactory serviceScopeFactory,
        IFcmService fcmService,
        IConfiguration configuration,
        ILogger<EscalationFiledWorkerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _fcmService = fcmService;
        _logger = logger;

        _factory = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EscalationFiledWorkerService starting");

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
                        var msg = JsonSerializer.Deserialize<EscalationFiledMessage>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (msg != null)
                            await ProcessMessageAsync(msg);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "EscalationFiledWorkerService: error processing message");
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

                _logger.LogInformation("EscalationFiledWorkerService consuming queue '{Queue}'", QueueName);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EscalationFiledWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("EscalationFiledWorkerService stopped");
    }

    private async Task ProcessMessageAsync(EscalationFiledMessage msg)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        const string adminTitle = "New agent complaint";
        var adminBody = $"{msg.ConsumerName} reported an issue with {msg.AgentName}: {msg.Reason}.";

        var adminTokens = (await unitOfWork.AdminDeviceTokens.GetAllValidAsync()).ToList();
        foreach (var token in adminTokens)
        {
            try
            {
                var ok = await _fcmService.SendAsync(token.Token, adminTitle, adminBody, "escalation");
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

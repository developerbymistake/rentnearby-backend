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

// Structural copy of RoleRequestedWorkerService: informational admin-only FCM nudge on its own queue.
public class StayJobPostedAdminWorkerService : BackgroundService
{
    private const string QueueName = StayJobPostedPublisher.AdminQueue;

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IFcmService _fcmService;
    private readonly ILogger<StayJobPostedAdminWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public StayJobPostedAdminWorkerService(
        IServiceScopeFactory serviceScopeFactory,
        IFcmService fcmService,
        IConfiguration configuration,
        ILogger<StayJobPostedAdminWorkerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _fcmService = fcmService;
        _logger = logger;

        _factory = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StayJobPostedAdminWorkerService starting");

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
                        var msg = JsonSerializer.Deserialize<StayJobPostedMessage>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (msg != null)
                            await ProcessMessageAsync(msg);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StayJobPostedAdminWorkerService: error processing message");
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

                _logger.LogInformation("StayJobPostedAdminWorkerService consuming queue '{Queue}'", QueueName);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StayJobPostedAdminWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("StayJobPostedAdminWorkerService stopped");
    }

    private async Task ProcessMessageAsync(StayJobPostedMessage msg)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var info = await (
            from j in db.StayJobs.AsNoTracking()
            join s in db.StayListings on j.StayListingId equals s.Id
            where j.Id == msg.JobId
            select new { RoleName = j.Role.Name, StayName = s.Name }).FirstOrDefaultAsync();

        if (info == null)
        {
            _logger.LogInformation("StayJobPostedAdminWorker: job {JobId} no longer exists, skipping push", msg.JobId);
            return;
        }

        const string adminTitle = "New job posted";
        var adminBody = $"{info.RoleName} at {info.StayName ?? "a stay"}";
        var wire = NotificationTypes.ToWireValue(NotificationTypes.StayJobPosted);
        var destination = new NotificationDestination(
            AdminNotificationRoutes.StayJobs,
            JsonSerializer.Serialize(new { id = msg.JobId }));

        var adminTokens = (await unitOfWork.AdminDeviceTokens.GetAllValidAsync()).ToList();
        foreach (var token in adminTokens)
        {
            try
            {
                var ok = await _fcmService.SendAsync(token.Token, adminTitle, adminBody, wire, destination);
                if (!ok) await unitOfWork.AdminDeviceTokens.MarkInvalidAsync(token.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FCM send failed for admin token, stay job {JobId}", msg.JobId);
            }
        }

        await unitOfWork.SaveChangesAsync();
    }
}

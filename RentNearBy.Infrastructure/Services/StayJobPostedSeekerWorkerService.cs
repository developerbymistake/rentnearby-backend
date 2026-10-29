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

// Push-only fan-out (no NotificationEvent row) to every active Seeker. SeekerPushedAt is an
// at-most-once claim, so a redelivered message is a no-op.
public class StayJobPostedSeekerWorkerService : BackgroundService
{
    private const string QueueName = StayJobPostedPublisher.SeekerQueue;
    private const int BatchSize = 500;
    private const int SendConcurrency = 50;

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IFcmService _fcmService;
    private readonly ILogger<StayJobPostedSeekerWorkerService> _logger;
    private readonly ConnectionFactory _factory;

    public StayJobPostedSeekerWorkerService(
        IServiceScopeFactory serviceScopeFactory,
        IFcmService fcmService,
        IConfiguration configuration,
        ILogger<StayJobPostedSeekerWorkerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _fcmService = fcmService;
        _logger = logger;

        _factory = new ConnectionFactory { Uri = new Uri(RabbitMqUrl.Build(configuration)) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StayJobPostedSeekerWorkerService starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

                // arguments: null — must match what the shared RabbitMqPublisher.PublishAsync
                // declares with (it has no way to pass custom queue arguments), or RabbitMQ
                // rejects the mismatched redeclare with a 406 and every publish silently fails.
                // No DLQ: a redelivery would hit the SeekerPushedAt claim and skip anyway.
                await channel.QueueDeclareAsync(
                    queue: QueueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: stoppingToken);

                // One message = one whole fan-out.
                await channel.BasicQosAsync(
                    prefetchSize: 0,
                    prefetchCount: 1,
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

                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StayJobPostedSeekerWorker: error processing message — nacking (dropped)");
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                    }
                };

                await channel.BasicConsumeAsync(
                    queue: QueueName,
                    autoAck: false,
                    consumer: consumer,
                    cancellationToken: stoppingToken);

                _logger.LogInformation("StayJobPostedSeekerWorkerService consuming queue '{Queue}'", QueueName);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StayJobPostedSeekerWorkerService connection lost, reconnecting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("StayJobPostedSeekerWorkerService stopped");
    }

    private async Task ProcessMessageAsync(StayJobPostedMessage msg)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var claimed = await db.StayJobs
            .Where(j => j.Id == msg.JobId && j.SeekerPushedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.SeekerPushedAt, DateTime.UtcNow));
        if (claimed == 0)
        {
            _logger.LogInformation("StayJobPostedSeekerWorker: job {JobId} already pushed or missing, skipping", msg.JobId);
            return;
        }

        var info = await (
            from j in db.StayJobs.AsNoTracking()
            join s in db.StayListings on j.StayListingId equals s.Id
            where j.Id == msg.JobId
            select new
            {
                j.Status,
                RoleName = j.Role.Name,
                StayName = s.Name,
                CityName = s.City != null ? s.City.Name : null,
                DistrictName = s.District != null ? s.District.Name : null,
                IsPublic = s.Status == StayStatuses.Approved && !s.IsDeleted && s.User.IsActive,
            }).FirstOrDefaultAsync();

        if (info == null || info.Status != StayJobStatuses.Active || !info.IsPublic)
        {
            _logger.LogInformation("StayJobPostedSeekerWorker: job {JobId} not public, skipping push", msg.JobId);
            return;
        }

        const string title = "New job near stays";
        var place = info.CityName ?? info.DistrictName;
        var body = string.IsNullOrWhiteSpace(place)
            ? $"{info.RoleName} at {info.StayName ?? "a stay"}"
            : $"{info.RoleName} at {info.StayName ?? "a stay"}, {place}";

        var destination = new NotificationDestination(
            UserNotificationRoutes.JobDetail,
            JsonSerializer.Serialize(new { id = msg.JobId }));
        var wire = NotificationTypes.ToWireValue(NotificationTypes.StayJobPosted);

        Guid? after = null;
        var totalSent = 0;
        var totalFailed = 0;

        while (true)
        {
            var batch = await unitOfWork.DeviceTokens.GetValidTokensByRoleAfterAsync(UserRoles.SeekerId, after, BatchSize);
            if (batch.Count == 0) break;

            var sent = 0;
            var failed = 0;

            foreach (var chunk in batch.Chunk(SendConcurrency))
            {
                // Sends run in parallel; invalid-token cleanup is applied sequentially below since
                // the DbContext behind unitOfWork isn't safe for concurrent writes.
                var results = await Task.WhenAll(chunk.Select(async t =>
                {
                    try
                    {
                        return (t.Token, ok: await _fcmService.SendAsync(t.Token, title, body, wire, destination));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StayJobPostedSeekerWorker: FCM send exception for job {JobId}", msg.JobId);
                        return (t.Token, ok: true); // transient error, not proof the token itself is invalid
                    }
                }));

                foreach (var (token, ok) in results)
                {
                    if (ok)
                    {
                        sent++;
                    }
                    else
                    {
                        failed++;
                        await unitOfWork.DeviceTokens.MarkInvalidAsync(token);
                    }
                }
            }

            await unitOfWork.SaveChangesAsync();

            totalSent += sent;
            totalFailed += failed;
            _logger.LogInformation(
                "StayJobPostedSeekerWorker: job {JobId} batch done — tokens={Tokens}, sent={Sent}, failed={Failed}",
                msg.JobId, batch.Count, sent, failed);

            after = batch[^1].Id;
            if (batch.Count < BatchSize) break;
        }

        _logger.LogInformation(
            "StayJobPostedSeekerWorker: job {JobId} fan-out complete — sent={Sent}, failed={Failed}",
            msg.JobId, totalSent, totalFailed);
    }
}

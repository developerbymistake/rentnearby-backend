using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RentNearBy.Core.Entities;

namespace RentNearBy.Infrastructure.Services;

public sealed record PushDeadLetter(string? Key, string? Kind, Guid? UserId);

// Consumes push.dead so a push that ran out of retries is seen: one critical log (EventId 7003) per message, then ack.
public sealed class PushDeadLetterWorkerService : BackgroundService
{
    public static readonly EventId DeadLetterEvent = new(7003, "push.dead");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConnectionFactory _factory;
    private readonly ILogger<PushDeadLetterWorkerService> _logger;

    public PushDeadLetterWorkerService(IConfiguration configuration, ILogger<PushDeadLetterWorkerService> logger)
    {
        _logger = logger;
        _factory = new ConnectionFactory
        {
            Uri = new Uri(RabbitMqUrl.Build(configuration)),
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            AutomaticRecoveryEnabled = true,
            ClientProvidedName = "rnb-push-dead-consumer",
        };
    }

    public static PushDeadLetter Read(ReadOnlySpan<byte> body)
    {
        try
        {
            var p = JsonSerializer.Deserialize<OutboxPushPayload>(Encoding.UTF8.GetString(body), Json);
            return p == null ? new PushDeadLetter(null, null, null) : new PushDeadLetter(p.Key, p.Kind, p.UserId);
        }
        catch (JsonException)
        {
            return new PushDeadLetter(null, null, null);
        }
    }

    public static void Log(ILogger logger, PushDeadLetter letter)
        => logger.LogCritical(DeadLetterEvent, "Push dead-lettered: key {Key}, kind {Kind}, user {UserId}",
            letter.Key ?? "unreadable", letter.Kind ?? "visible", letter.UserId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await RabbitTopology.DeclareAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, 20, false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    Log(_logger, Read(ea.Body.Span));
                    await channel.BasicAckAsync(ea.DeliveryTag, false, stoppingToken);
                };
                await channel.BasicConsumeAsync(RabbitTopology.DeadQueue, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
                _logger.LogInformation("PushDeadLetterWorkerService consuming '{Queue}'", RabbitTopology.DeadQueue);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PushDeadLetterWorkerService connection lost, reconnecting in 5 seconds");
                try { await Task.Delay(5000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}

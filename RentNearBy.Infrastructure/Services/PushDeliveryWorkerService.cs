using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;

namespace RentNearBy.Infrastructure.Services;

public sealed record PushDelivery(ulong Tag, OutboxPushPayload? Payload, int Attempt);

public enum PushVerdict { Ack, Retry, Dead }

public interface IPushDeliveryStore
{
    Task<IReadOnlyList<(Guid UserId, string Token)>> GetTokensAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, string>> GetNotificationTypesAsync(IReadOnlyCollection<Guid> notificationIds, CancellationToken ct);
    Task MarkInvalidAsync(IReadOnlyCollection<string> tokens, CancellationToken ct);
}

public sealed class PushDeliveryStore(IServiceScopeFactory scopeFactory) : IPushDeliveryStore
{
    public async Task<IReadOnlyList<(Guid UserId, string Token)>> GetTokensAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        return await new DeviceTokenRepository(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()).GetValidByUserIdsAsync(userIds, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNotificationTypesAsync(IReadOnlyCollection<Guid> notificationIds, CancellationToken ct)
    {
        if (notificationIds.Count == 0) return new Dictionary<Guid, string>();
        var ids = notificationIds.ToArray();
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.NotificationEvents.AsNoTracking().Where(n => ids.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Type, ct);
    }

    public async Task MarkInvalidAsync(IReadOnlyCollection<string> tokens, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        await new DeviceTokenRepository(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()).MarkInvalidManyAsync(tokens, ct);
    }
}

// One batch: one token query, one NotificationEvent query, FCM in chunks of 500, one UPDATE for dead tokens.
// A message is delivered when any of its tokens took it; only an all-transient failure retries (so a three-device user never gets duplicates).
public static class PushBatchProcessor
{
    public static async Task<IReadOnlyList<PushVerdict>> ProcessAsync(
        IReadOnlyList<PushDelivery> batch, IPushDeliveryStore store, IFcmService fcm, ILogger logger, CancellationToken ct)
    {
        var verdicts = new PushVerdict[batch.Count];
        var live = new List<int>(batch.Count);
        for (var i = 0; i < batch.Count; i++)
        {
            if (batch[i].Payload is null || batch[i].Payload!.UserId == Guid.Empty) verdicts[i] = PushVerdict.Dead;
            else live.Add(i);
        }
        if (live.Count == 0) return verdicts;

        var userIds = live.Select(i => batch[i].Payload!.UserId).Distinct().ToList();
        var eventIds = live.Select(i => batch[i].Payload!.NotificationId).Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
        var tokenRows = await store.GetTokensAsync(userIds, ct);
        var tokens = tokenRows.ToLookup(r => r.UserId, r => r.Token);
        var types = await store.GetNotificationTypesAsync(eventIds, ct);

        var specs = new List<FcmMessageSpec>();
        var owner = new List<int>();
        foreach (var i in live)
        {
            var p = batch[i].Payload!;
            if (p.NotificationId is { } nid && !types.ContainsKey(nid)) { verdicts[i] = PushVerdict.Ack; continue; }
            var userTokens = tokens[p.UserId].Distinct().ToList();
            if (userTokens.Count == 0) { verdicts[i] = PushVerdict.Ack; continue; }
            var data = new Dictionary<string, string>
            {
                ["notification_type"] = NotificationTypes.ToWireValue(p.NotificationId is { } id ? types[id] : NotificationTypes.TripReminder),
                ["key"] = p.Key,
            };
            if (p.Route != null) data["action_route"] = p.Route;
            if (p.ArgsJson != null) data["action_args_json"] = p.ArgsJson;
            foreach (var token in userTokens)
            {
                specs.Add(new FcmMessageSpec(token, p.Title, p.Body, data, string.IsNullOrEmpty(p.Tag) ? p.Key : p.Tag));
                owner.Add(i);
            }
        }
        if (specs.Count == 0) return verdicts;

        IReadOnlyList<FcmSendOutcome> outcomes;
        try
        {
            outcomes = await fcm.SendEachAsync(specs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "FCM batch call failed for {Count} sends", specs.Count);
            outcomes = Enumerable.Repeat(FcmSendOutcome.Failed, specs.Count).ToList();
        }

        var sent = new HashSet<int>();
        var transient = new HashSet<int>();
        var dead = new HashSet<string>();
        for (var s = 0; s < specs.Count; s++)
        {
            var outcome = s < outcomes.Count ? outcomes[s] : FcmSendOutcome.Failed;
            if (outcome == FcmSendOutcome.Sent) sent.Add(owner[s]);
            else if (outcome == FcmSendOutcome.InvalidToken) dead.Add(specs[s].Token);
            else transient.Add(owner[s]);
        }
        foreach (var i in owner.Distinct())
            verdicts[i] = sent.Contains(i) || !transient.Contains(i) ? PushVerdict.Ack : PushVerdict.Retry;

        if (dead.Count > 0)
        {
            try { await store.MarkInvalidAsync(dead, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Could not mark {Count} dead tokens", dead.Count); }
        }
        return verdicts;
    }
}

// Consumes push.send: prefetch 200, up to 200 messages or 150 ms per batch, two batch loops. Retry hops go through the confirmed
// publisher (30 s, 5 min, then push.dead) before the original is acked. Delivery is at-least-once; the FCM tag lets a duplicate replace.
public sealed class PushDeliveryWorkerService : BackgroundService
{
    public const int Prefetch = 200, MaxBatch = 200, BatchWaitMs = 150, Loops = 2;

    private readonly ConnectionFactory _factory;
    private readonly IPushDeliveryStore _store;
    private readonly IFcmService _fcm;
    private readonly IConfirmedPublisher _publisher;
    private readonly ILogger<PushDeliveryWorkerService> _logger;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PushDeliveryWorkerService(
        IConfiguration configuration, IPushDeliveryStore store, IFcmService fcm, IConfirmedPublisher publisher, ILogger<PushDeliveryWorkerService> logger)
    {
        _store = store;
        _fcm = fcm;
        _publisher = publisher;
        _logger = logger;
        _factory = new ConnectionFactory
        {
            Uri = new Uri(RabbitMqUrl.Build(configuration)),
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            AutomaticRecoveryEnabled = true,
            ClientProvidedName = "rnb-push-consumer",
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(false, false, null, consumerDispatchConcurrency: 2), cancellationToken: stoppingToken);
                await RabbitTopology.DeclareAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, Prefetch, false, stoppingToken);

                var inbox = Channel.CreateUnbounded<PushDelivery>();
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, ea) =>
                {
                    inbox.Writer.TryWrite(Parse(ea));
                    return Task.CompletedTask;
                };
                await channel.BasicConsumeAsync(RabbitTopology.SendQueue, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
                _logger.LogInformation("PushDeliveryWorkerService consuming '{Queue}'", RabbitTopology.SendQueue);

                var loops = Enumerable.Range(0, Loops).Select(_ => LoopAsync(channel, inbox.Reader, stoppingToken)).ToArray();
                await Task.WhenAny(loops);
                if (stoppingToken.IsCancellationRequested) break;
                throw new InvalidOperationException("push delivery loop stopped");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PushDeliveryWorkerService connection lost, reconnecting in 5 seconds");
                try { await Task.Delay(5000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task LoopAsync(IChannel channel, ChannelReader<PushDelivery> reader, CancellationToken ct)
    {
        while (await reader.WaitToReadAsync(ct))
        {
            var batch = new List<PushDelivery>(MaxBatch);
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(BatchWaitMs);
            try
            {
                while (batch.Count < MaxBatch)
                {
                    if (reader.TryRead(out var next)) { batch.Add(next); continue; }
                    if (batch.Count == 0 || !await reader.WaitToReadAsync(window.Token)) break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            if (batch.Count > 0) await HandleBatchAsync(channel, batch, ct);
        }
    }

    private async Task HandleBatchAsync(IChannel channel, List<PushDelivery> batch, CancellationToken ct)
    {
        IReadOnlyList<PushVerdict> verdicts;
        try
        {
            verdicts = await PushBatchProcessor.ProcessAsync(batch, _store, _fcm, _logger, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Push batch of {Count} failed before sending, retrying all", batch.Count);
            verdicts = Enumerable.Repeat(PushVerdict.Retry, batch.Count).ToList();
        }

        var hops = new List<(PushDelivery Delivery, ConfirmedMessage Message)>();
        for (var i = 0; i < batch.Count; i++)
        {
            if (verdicts[i] == PushVerdict.Ack) { await channel.BasicAckAsync(batch[i].Tag, false, ct); continue; }
            var failures = batch[i].Attempt + 1;
            var route = verdicts[i] == PushVerdict.Dead ? RabbitTopology.DeadKey : RabbitTopology.NextRoute(failures);
            var body = batch[i].Payload is { } p ? JsonSerializer.Serialize(p with { Attempt = failures }, Json) : string.Empty;
            hops.Add((batch[i], new ConfirmedMessage(RabbitTopology.Exchange, route, body, batch[i].Payload?.Key ?? Guid.NewGuid().ToString("N"), failures)));
        }
        if (hops.Count == 0) return;

        var results = await _publisher.PublishBatchAsync(hops.Select(h => h.Message).ToList(), ct);
        for (var i = 0; i < hops.Count; i++)
        {
            if (results[i].Confirmed) await channel.BasicAckAsync(hops[i].Delivery.Tag, false, ct);
            else await channel.BasicNackAsync(hops[i].Delivery.Tag, false, true, ct);
        }
    }

    private static PushDelivery Parse(BasicDeliverEventArgs ea)
    {
        var attempt = 0;
        if (ea.BasicProperties.Headers?.TryGetValue(RabbitTopology.AttemptHeader, out var h) == true)
            attempt = h switch { int i => i, long l => (int)l, byte b => b, short s => s, _ => 0 };
        try
        {
            var payload = JsonSerializer.Deserialize<OutboxPushPayload>(Encoding.UTF8.GetString(ea.Body.Span), Json);
            return new PushDelivery(ea.DeliveryTag, payload, attempt);
        }
        catch (JsonException)
        {
            return new PushDelivery(ea.DeliveryTag, null, attempt);
        }
    }
}

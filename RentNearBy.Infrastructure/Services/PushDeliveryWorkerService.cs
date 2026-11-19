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
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
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

    // Ring offers among the ids that are no longer Ringing (answered, closed or gone).
    Task<IReadOnlySet<Guid>> GetEndedRingOffersAsync(IReadOnlyCollection<Guid> offerIds, CancellationToken ct);
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

    public async Task<IReadOnlySet<Guid>> GetEndedRingOffersAsync(IReadOnlyCollection<Guid> offerIds, CancellationToken ct)
    {
        if (offerIds.Count == 0) return new HashSet<Guid>();
        var ids = offerIds.ToArray();
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ringing = await db.Set<TaxiRequestOffer>().AsNoTracking()
            .Where(o => ids.Contains(o.Id) && o.Status == OfferStates.Ringing).Select(o => o.Id).ToListAsync(ct);
        return ids.Except(ringing).ToHashSet();
    }
}

// One batch: one token query, one NotificationEvent query, FCM in chunks of 500, one UPDATE for dead tokens.
// A message is delivered when any of its tokens took it; only an all-transient failure retries (so a three-device user never gets duplicates).
// A payload past its ExpiresAtUtc is acked and counted, never sent.
// Ring and ring end data carry no key (contract 10); a ring's expiresAt is the payload expiry, which FcmService turns into the TTL,
// and its sentAt is this send (a retry hop too), so the app's clock offset is measured against the moment the TTL starts.
public static class PushBatchProcessor
{
    public static Task<IReadOnlyList<PushVerdict>> ProcessAsync(
        IReadOnlyList<PushDelivery> batch, IPushDeliveryStore store, IFcmService fcm, ILogger logger, CancellationToken ct)
        => ProcessAsync(batch, store, fcm, logger, DateTime.UtcNow, ct);

    public static async Task<IReadOnlyList<PushVerdict>> ProcessAsync(
        IReadOnlyList<PushDelivery> batch, IPushDeliveryStore store, IFcmService fcm, ILogger logger, DateTime utcNow, CancellationToken ct)
    {
        var verdicts = new PushVerdict[batch.Count];
        var live = new List<int>(batch.Count);
        var expired = 0;
        for (var i = 0; i < batch.Count; i++)
        {
            var p = batch[i].Payload;
            if (p is null || p.UserId == Guid.Empty) verdicts[i] = PushVerdict.Dead;
            else if (IsExpired(p, utcNow)) { verdicts[i] = PushVerdict.Ack; expired++; }
            else live.Add(i);
        }
        if (expired > 0) logger.LogInformation("Dropped {Count} expired pushes without sending", expired);
        if (live.Count == 0) return verdicts;

        // A ring whose offer the driver already answered (in-app list) or that already closed is acked, never sent.
        var ringOffers = live.Select(i => RingOfferId(batch[i].Payload!)).Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
        if (ringOffers.Count > 0)
        {
            var ended = await store.GetEndedRingOffersAsync(ringOffers, ct);
            if (ended.Count > 0)
            {
                var before = live.Count;
                live.RemoveAll(i =>
                {
                    if (RingOfferId(batch[i].Payload!) is not { } id || !ended.Contains(id)) return false;
                    verdicts[i] = PushVerdict.Ack;
                    return true;
                });
                logger.LogInformation("Dropped {Count} rings of offers no longer ringing", before - live.Count);
                if (live.Count == 0) return verdicts;
            }
        }

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
            var data = new Dictionary<string, string>();
            if (p.Data != null)
                foreach (var kv in p.Data)
                    if (kv.Value != null) data[kv.Key] = kv.Value;
            var type = TypeOf(p, types);
            data["notification_type"] = NotificationTypes.ToWireValue(type);
            if (type == NotificationTypes.TaxiRequestRing)
            {
                data.Remove("key");
                if (p.ExpiresAtUtc is { } expires) data["expiresAt"] = TaxiRingPayload.Instant(expires);
                data["sentAt"] = TaxiRingPayload.Instant(utcNow);
            }
            else if (type == NotificationTypes.TaxiRequestRingEnded) data.Remove("key");
            else data["key"] = p.Key;
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

    public static bool IsExpired(OutboxPushPayload payload, DateTime utcNow)
        => payload.ExpiresAtUtc is { } expires && expires <= utcNow;

    // The offer of a real ring; a test ring (empty offerId) and every other push have none.
    public static Guid? RingOfferId(OutboxPushPayload payload)
        => payload.Kind == NotificationTypes.TaxiRequestRing && payload.NotificationId is null
            && payload.Data?.GetValueOrDefault("offerId") is { } raw && Guid.TryParse(raw, out var id) && id != Guid.Empty ? id : null;

    private static string TypeOf(OutboxPushPayload p, IReadOnlyDictionary<Guid, string> types)
        => p.NotificationId is { } id ? types[id]
            : !string.IsNullOrWhiteSpace(p.Kind) ? p.Kind
            : NotificationTypes.TripReminder;

    // Where a message that was not acked goes next; null = ack and drop (an expiring push past its last useful retry).
    public static string? HopRoute(PushDelivery delivery, PushVerdict verdict, DateTime utcNow)
    {
        if (verdict == PushVerdict.Ack) return null;
        if (verdict == PushVerdict.Dead || delivery.Payload is null) return RabbitTopology.DeadKey;
        return RabbitTopology.NextRoute(delivery.Attempt + 1, delivery.Payload.ExpiresAtUtc, utcNow);
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
    private readonly WorkerHeartbeats? _heartbeats;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PushDeliveryWorkerService(
        IConfiguration configuration, IPushDeliveryStore store, IFcmService fcm, IConfirmedPublisher publisher, ILogger<PushDeliveryWorkerService> logger,
        WorkerHeartbeats? heartbeats = null)
    {
        _heartbeats = heartbeats;
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
                await Task.WhenAny(loops.Append(BeatWhileConnectedAsync(channel, stoppingToken)));
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

    // The loops idle when the queue is empty, so liveness is a timer that beats only while the channel is open.
    private async Task BeatWhileConnectedAsync(IChannel channel, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            if (channel.IsOpen) _heartbeats?.Beat(WorkerNames.PushDelivery);
        }
        while (await timer.WaitForNextTickAsync(ct));
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
        var now = DateTime.UtcNow;
        var dropped = 0;
        for (var i = 0; i < batch.Count; i++)
        {
            var route = PushBatchProcessor.HopRoute(batch[i], verdicts[i], now);
            if (route == null)
            {
                if (verdicts[i] != PushVerdict.Ack) dropped++;
                await channel.BasicAckAsync(batch[i].Tag, false, ct);
                continue;
            }
            var failures = batch[i].Attempt + 1;
            var body = batch[i].Payload is { } p ? JsonSerializer.Serialize(p with { Attempt = failures }, Json) : string.Empty;
            hops.Add((batch[i], new ConfirmedMessage(RabbitTopology.Exchange, route, body, batch[i].Payload?.Key ?? Guid.NewGuid().ToString("N"), failures)));
        }
        if (dropped > 0) _logger.LogInformation("Dropped {Count} expiring pushes that would outlive their retry", dropped);
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

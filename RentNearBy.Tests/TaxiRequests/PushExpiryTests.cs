using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.TaxiRequests;

public class PushExpiryTests
{
    private static readonly Guid U1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

    private sealed class FakeStore(Dictionary<Guid, string[]> tokens, HashSet<Guid>? ended = null) : IPushDeliveryStore
    {
        public int TokenQueries { get; private set; }
        public readonly List<Guid[]> EndedQueries = [];

        public Task<IReadOnlySet<Guid>> GetEndedRingOffersAsync(IReadOnlyCollection<Guid> offerIds, CancellationToken ct)
        {
            EndedQueries.Add(offerIds.ToArray());
            return Task.FromResult<IReadOnlySet<Guid>>(offerIds.Where(id => ended?.Contains(id) == true).ToHashSet());
        }

        public Task<IReadOnlyList<(Guid UserId, string Token)>> GetTokensAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
        {
            TokenQueries++;
            return Task.FromResult<IReadOnlyList<(Guid, string)>>(tokens.Where(k => userIds.Contains(k.Key)).SelectMany(k => k.Value.Select(t => (k.Key, t))).ToList());
        }

        public Task<IReadOnlyDictionary<Guid, string>> GetNotificationTypesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task MarkInvalidAsync(IReadOnlyCollection<string> t, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeFcm(FcmSendOutcome outcome = FcmSendOutcome.Sent) : IFcmService
    {
        public List<FcmMessageSpec> Sent { get; } = [];

        public Task<IReadOnlyList<FcmSendOutcome>> SendEachAsync(IReadOnlyList<FcmMessageSpec> messages, CancellationToken ct = default)
        {
            Sent.AddRange(messages);
            return Task.FromResult<IReadOnlyList<FcmSendOutcome>>(messages.Select(_ => outcome).ToList());
        }

        public Task<bool> SendAsync(string token, string title, string body, string notificationType, IDictionary<string, string>? data = null) => throw new NotSupportedException();
        public Task<bool> SendAsync(string token, string title, string body, string notificationType, NotificationDestination destination) => throw new NotSupportedException();
        public Task<bool> SendToTopicAsync(string topic, string title, string body, IDictionary<string, string>? data = null) => throw new NotSupportedException();
    }

    private static PushDelivery Ring(DateTime? expiresAt, int attempt = 0, string? kind = "TaxiRequestRing", IReadOnlyDictionary<string, string>? data = null)
        => new(1, new OutboxPushPayload("ring:1", U1, null, "New trip request", "Tap to see it", "/taxi-request-ring", "{}", "ring:1", attempt, kind, expiresAt, data), attempt);

    private static Task<IReadOnlyList<PushVerdict>> Run(PushDelivery delivery, FakeStore store, FakeFcm fcm)
        => PushBatchProcessor.ProcessAsync([delivery], store, fcm, NullLogger.Instance, Now, CancellationToken.None);

    [Fact]
    public async Task Expired_payload_is_acked_not_sent()
    {
        var store = new FakeStore(new() { [U1] = ["a"] });
        var fcm = new FakeFcm();

        var verdicts = await Run(Ring(Now.AddSeconds(-1)), store, fcm);

        Assert.Equal([PushVerdict.Ack], verdicts);
        Assert.Empty(fcm.Sent);
        Assert.Equal(0, store.TokenQueries);
        Assert.Null(PushBatchProcessor.HopRoute(Ring(Now.AddSeconds(-1)), verdicts[0], Now));
    }

    [Fact]
    public async Task Expiry_at_exactly_now_counts_as_expired_and_a_live_one_is_sent()
    {
        var fcm = new FakeFcm();
        Assert.Equal([PushVerdict.Ack], await Run(Ring(Now), new FakeStore(new() { [U1] = ["a"] }), fcm));
        Assert.Empty(fcm.Sent);

        Assert.Equal([PushVerdict.Ack], await Run(Ring(Now.AddSeconds(60)), new FakeStore(new() { [U1] = ["a"] }), fcm));
        Assert.Single(fcm.Sent);
    }

    [Fact]
    public async Task Ring_retry_uses_only_the_30s_step()
    {
        var verdicts = await Run(Ring(Now.AddSeconds(45)), new FakeStore(new() { [U1] = ["a"] }), new FakeFcm(FcmSendOutcome.Failed));
        Assert.Equal([PushVerdict.Retry], verdicts);

        Assert.Equal(RabbitTopology.Retry30Key, PushBatchProcessor.HopRoute(Ring(Now.AddSeconds(45), attempt: 0), PushVerdict.Retry, Now));
        Assert.Null(PushBatchProcessor.HopRoute(Ring(Now.AddHours(1), attempt: 1), PushVerdict.Retry, Now));
        Assert.Null(PushBatchProcessor.HopRoute(Ring(Now.AddHours(1), attempt: 2), PushVerdict.Retry, Now));
        Assert.Equal(RabbitTopology.Retry30Key, RabbitTopology.NextRoute(1, Now.AddHours(1), Now));
        Assert.Null(RabbitTopology.NextRoute(2, Now.AddHours(1), Now));
    }

    [Fact]
    public async Task Ring_of_an_offer_no_longer_ringing_is_acked_not_sent()
    {
        var answered = Guid.NewGuid();
        var ringing = Guid.NewGuid();
        var store = new FakeStore(new() { [U1] = ["a"] }, [answered]);
        var fcm = new FakeFcm();
        PushDelivery RingOf(Guid offer) => Ring(Now.AddSeconds(200), attempt: 1, data: new Dictionary<string, string> { ["offerId"] = offer.ToString() });

        var verdicts = await PushBatchProcessor.ProcessAsync([RingOf(answered), RingOf(ringing)], store, fcm, NullLogger.Instance, Now, CancellationToken.None);

        Assert.Equal([PushVerdict.Ack, PushVerdict.Ack], verdicts);
        Assert.Equal(ringing.ToString(), Assert.Single(fcm.Sent).Data["offerId"]);
        Assert.Single(store.EndedQueries);
        Assert.Equal(2, store.EndedQueries[0].Length);
    }

    [Fact]
    public async Task Test_ring_and_other_pushes_never_query_offer_state()
    {
        var store = new FakeStore(new() { [U1] = ["a"] });
        var fcm = new FakeFcm();

        await Run(Ring(Now.AddSeconds(30), data: new Dictionary<string, string> { ["offerId"] = "" }), store, fcm);
        await Run(Ring(null, kind: null, data: new Dictionary<string, string> { ["offerId"] = Guid.NewGuid().ToString() }), store, fcm);

        Assert.Empty(store.EndedQueries);
        Assert.Equal(2, fcm.Sent.Count);
    }

    [Fact]
    public void Ring_retry_past_expiry_is_dropped()
    {
        Assert.Null(PushBatchProcessor.HopRoute(Ring(Now.AddSeconds(30)), PushVerdict.Retry, Now));
        Assert.Null(PushBatchProcessor.HopRoute(Ring(Now.AddSeconds(10)), PushVerdict.Retry, Now));
        Assert.Null(RabbitTopology.NextRoute(1, Now.AddSeconds(29), Now));
    }

    [Fact]
    public void A_push_without_expiry_keeps_the_full_ladder()
    {
        Assert.Equal(RabbitTopology.Retry30Key, PushBatchProcessor.HopRoute(Ring(null, attempt: 0), PushVerdict.Retry, Now));
        Assert.Equal(RabbitTopology.Retry5mKey, PushBatchProcessor.HopRoute(Ring(null, attempt: 1), PushVerdict.Retry, Now));
        Assert.Equal(RabbitTopology.DeadKey, PushBatchProcessor.HopRoute(Ring(null, attempt: 2), PushVerdict.Retry, Now));
        Assert.Equal(RabbitTopology.DeadKey, PushBatchProcessor.HopRoute(new PushDelivery(1, null, 0), PushVerdict.Dead, Now));
        Assert.Null(PushBatchProcessor.HopRoute(Ring(null), PushVerdict.Ack, Now));
    }

    [Fact]
    public async Task Kind_sets_the_notification_type()
    {
        var fcm = new FakeFcm();
        await Run(Ring(Now.AddSeconds(60), kind: "TaxiRequestRing"), new FakeStore(new() { [U1] = ["a"] }), fcm);
        Assert.Equal(NotificationTypes.ToWireValue("TaxiRequestRing"), Assert.Single(fcm.Sent).Data["notification_type"]);

        var plain = new FakeFcm();
        await Run(Ring(null, kind: null), new FakeStore(new() { [U1] = ["a"] }), plain);
        Assert.Equal("trip_reminder", Assert.Single(plain.Sent).Data["notification_type"]);
    }

    [Fact]
    public async Task Data_entries_are_copied_but_never_override_the_reserved_keys()
    {
        var fcm = new FakeFcm();
        var data = new Dictionary<string, string> { ["offer_id"] = "o1", ["key"] = "spoof", ["notification_type"] = "spoof" };
        await Run(Ring(Now.AddSeconds(60), kind: null, data: data), new FakeStore(new() { [U1] = ["a"] }), fcm);

        var sent = Assert.Single(fcm.Sent).Data;
        Assert.Equal("o1", sent["offer_id"]);
        Assert.Equal("ring:1", sent["key"]);
        Assert.NotEqual("spoof", sent["notification_type"]);

        var ring = new FakeFcm();
        await Run(Ring(Now.AddSeconds(60), data: data), new FakeStore(new() { [U1] = ["a"] }), ring);
        var ringData = Assert.Single(ring.Sent).Data;
        Assert.Equal("o1", ringData["offer_id"]);
        Assert.False(ringData.ContainsKey("key"));
        Assert.Equal(NotificationTypes.ToWireValue(NotificationTypes.TaxiRequestRing), ringData["notification_type"]);
    }

    [Fact]
    public async Task Retried_ring_carries_the_time_of_this_send_as_sentAt()
    {
        var written = Now.AddSeconds(-30);
        var data = new Dictionary<string, string> { ["sentAt"] = TaxiRingPayload.Instant(written), ["expiresAt"] = TaxiRingPayload.Instant(Now.AddSeconds(10)) };
        var fcm = new FakeFcm();

        await Run(Ring(Now.AddSeconds(10), attempt: 1, data: data), new FakeStore(new() { [U1] = ["a"] }), fcm);

        var sent = Assert.Single(fcm.Sent).Data;
        Assert.Equal(TaxiRingPayload.Instant(Now), sent["sentAt"]);
        Assert.Equal(TaxiRingPayload.Instant(Now.AddSeconds(10)), sent["expiresAt"]);
        Assert.Equal(10, FcmService.RingTtlSeconds(sent, Now));
        Assert.Equal(TaxiRingPayload.Instant(written), data["sentAt"]);
    }

    [Fact]
    public async Task Only_a_ring_gets_sentAt_rewritten()
    {
        var fcm = new FakeFcm();
        var data = new Dictionary<string, string> { ["sentAt"] = "keep" };

        await Run(Ring(Now.AddSeconds(60), kind: null, data: data), new FakeStore(new() { [U1] = ["a"] }), fcm);

        Assert.Equal("keep", Assert.Single(fcm.Sent).Data["sentAt"]);
    }

    [Fact]
    public async Task Null_data_values_are_skipped()
    {
        var p = JsonSerializer.Deserialize<OutboxPushPayload>(
            "{\"key\":\"k\",\"userId\":\"00000000-0000-0000-0000-000000000001\",\"title\":\"T\",\"body\":\"B\",\"tag\":\"k\",\"attempt\":0,\"data\":{\"a\":null,\"b\":\"x\"}}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var fcm = new FakeFcm();

        await Run(new PushDelivery(1, p, 0), new FakeStore(new() { [U1] = ["a"] }), fcm);

        var sent = Assert.Single(fcm.Sent).Data;
        Assert.False(sent.ContainsKey("a"));
        Assert.Equal("x", sent["b"]);
        Assert.DoesNotContain(sent.Values, v => v == null);
    }

    [Fact]
    public void Old_payload_json_still_parses()
    {
        const string old = "{\"key\":\"rem:1\",\"userId\":\"00000000-0000-0000-0000-000000000001\",\"notificationId\":null,"
            + "\"title\":\"T\",\"body\":\"B\",\"route\":\"/trip-plan\",\"argsJson\":\"{}\",\"tag\":\"rem:1\",\"attempt\":1}";
        var p = JsonSerializer.Deserialize<OutboxPushPayload>(old, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal("rem:1", p.Key);
        Assert.Equal(U1, p.UserId);
        Assert.Equal(1, p.Attempt);
        Assert.Null(p.Kind);
        Assert.Null(p.ExpiresAtUtc);
        Assert.Null(p.Data);

        var roundTrip = JsonSerializer.Deserialize<OutboxPushPayload>(
            JsonSerializer.Serialize(p with { Kind = "TaxiRequestRing", ExpiresAtUtc = Now, Data = new Dictionary<string, string> { ["a"] = "b" } },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("TaxiRequestRing", roundTrip.Kind);
        Assert.Equal(Now, roundTrip.ExpiresAtUtc);
        Assert.Equal("b", roundTrip.Data!["a"]);
    }

    [Fact]
    public void Dead_letter_reader_pulls_key_kind_and_user_and_survives_garbage()
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(Ring(Now).Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(new PushDeadLetter("ring:1", "TaxiRequestRing", U1), PushDeadLetterWorkerService.Read(body));
        Assert.Equal(new PushDeadLetter(null, null, null), PushDeadLetterWorkerService.Read("not json"u8));
        Assert.Equal(7003, PushDeadLetterWorkerService.DeadLetterEvent.Id);

        var logger = new ListLogger();
        PushDeadLetterWorkerService.Log(logger, new PushDeadLetter("ring:1", "TaxiRequestRing", U1));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Equal(7003, entry.Event.Id);
        Assert.Contains("ring:1", entry.Message);
        Assert.Contains("TaxiRequestRing", entry.Message);
        Assert.Contains(U1.ToString(), entry.Message);
    }

    private static PushDelivery RingEnd(string? exceptToken)
        => new(1, new OutboxPushPayload("ringend:1", U1, null, "Request closed", "You accepted this request", "/taxi-request-ring", "{}",
            "taxi_ring_1", 0, "TaxiRequestRingEnded", Now.AddSeconds(60), new Dictionary<string, string> { ["kind"] = "taxi_ring_end" }, exceptToken), 0);

    // O11 + O14: the answering phone never receives the ring end of its own answer; only the driver's other tokens do.
    [Fact]
    public async Task Ring_end_skips_the_answering_token_and_reaches_the_other_devices()
    {
        var fcm = new FakeFcm();
        var verdicts = await Run(RingEnd("phone-a"), new FakeStore(new() { [U1] = ["phone-a", "phone-b", "phone-c"] }), fcm);

        Assert.Equal([PushVerdict.Ack], verdicts);
        Assert.Equal(["phone-b", "phone-c"], fcm.Sent.Select(m => m.Token).ToArray());
    }

    [Fact]
    public async Task Ring_end_with_only_the_answering_token_is_acked_and_never_sent()
    {
        var fcm = new FakeFcm();
        var verdicts = await Run(RingEnd("phone-a"), new FakeStore(new() { [U1] = ["phone-a"] }), fcm);

        Assert.Equal([PushVerdict.Ack], verdicts);
        Assert.Empty(fcm.Sent);
    }

    [Fact]
    public async Task Push_without_an_except_token_goes_to_every_token()
    {
        var fcm = new FakeFcm();
        await Run(RingEnd(null), new FakeStore(new() { [U1] = ["phone-a", "phone-b"] }), fcm);

        Assert.Equal(["phone-a", "phone-b"], fcm.Sent.Select(m => m.Token).ToArray());
    }

    [Fact]
    public void Old_outbox_row_without_except_token_still_parses()
    {
        var old = "{\"key\":\"ringend:1\",\"userId\":\"00000000-0000-0000-0000-000000000001\",\"title\":\"t\",\"body\":\"b\",\"tag\":\"x\"}";
        var payload = JsonSerializer.Deserialize<OutboxPushPayload>(old, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(payload.ExceptToken);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, EventId Event, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId, formatter(state, exception)));
    }
}

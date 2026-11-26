using System.Text.Json;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.TaxiRequests;

public class FcmRingMessageTests
{
    private static readonly Guid Driver = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid OfferId = Guid.Parse("a1b2c3d4-0001-4a00-8000-000000000001");

    private sealed class FakeStore : IPushDeliveryStore
    {
        public Task<IReadOnlyList<(Guid UserId, string Token)>> GetTokensAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(Guid, string)>>([(Driver, "tok-1")]);

        public Task<IReadOnlyDictionary<Guid, string>> GetNotificationTypesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task MarkInvalidAsync(IReadOnlyCollection<string> t, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlySet<Guid>> GetEndedRingOffersAsync(IReadOnlyCollection<Guid> offerIds, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlySet<Guid>> GetSeekersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<Guid>>(userIds.ToHashSet());
    }

    private sealed class FakeFcm : IFcmService
    {
        public List<FcmMessageSpec> Sent { get; } = [];

        public Task<IReadOnlyList<FcmSendOutcome>> SendEachAsync(IReadOnlyList<FcmMessageSpec> messages, CancellationToken ct = default)
        {
            Sent.AddRange(messages);
            return Task.FromResult<IReadOnlyList<FcmSendOutcome>>(messages.Select(_ => FcmSendOutcome.Sent).ToList());
        }

        public Task<bool> SendAsync(string token, string title, string body, string notificationType, IDictionary<string, string>? data = null) => throw new NotSupportedException();
        public Task<bool> SendAsync(string token, string title, string body, string notificationType, NotificationDestination destination) => throw new NotSupportedException();
        public Task<bool> SendToTopicAsync(string topic, string title, string body, IDictionary<string, string>? data = null) => throw new NotSupportedException();
    }

    private static JsonElement Sample(string name)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, name))).RootElement;

    private static Dictionary<string, string> DataOf(JsonElement sample)
        => sample.GetProperty("data").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

    private static DateTime Utc(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

    // The payload exactly as TaxiPushWriter writes it, sent through the real batch processor.
    private static async Task<(FcmMessageSpec Spec, DateTime SentAt)> RingSpecAsync()
    {
        var data = DataOf(Sample("push_ring.json"));
        var sentAt = Utc(data["sentAt"]);
        var payload = new OutboxPushPayload(
            TaxiPushKinds.RingKey(OfferId), Driver, null, TaxiRingPayload.RingTitle, data["line1"], data["action_route"], data["action_args_json"],
            TaxiPushKinds.RingTag(OfferId), 0, NotificationTypes.TaxiRequestRing, Utc(data["expiresAt"]), data);
        return (await SpecAsync(payload, sentAt), sentAt);
    }

    private static async Task<FcmMessageSpec> RingEndSpecAsync(DateTime now)
    {
        var sample = Sample("push_ring_end.json");
        var data = DataOf(sample);
        var n = sample.GetProperty("notification");
        var payload = new OutboxPushPayload(
            TaxiPushKinds.RingEndKey(OfferId), Driver, null, n.GetProperty("title").GetString()!, n.GetProperty("body").GetString()!,
            data["action_route"], data["action_args_json"], TaxiPushKinds.RingTag(OfferId), 0, NotificationTypes.TaxiRequestRingEnded, now.AddSeconds(60), data);
        return await SpecAsync(payload, now);
    }

    private static async Task<FcmMessageSpec> SpecAsync(OutboxPushPayload payload, DateTime now)
    {
        var fcm = new FakeFcm();
        var verdicts = await PushBatchProcessor.ProcessAsync([new PushDelivery(1, payload, 0)], new FakeStore(), fcm, NullLogger.Instance, now, CancellationToken.None);
        Assert.Equal([PushVerdict.Ack], verdicts);
        return Assert.Single(fcm.Sent);
    }

    [Fact]
    public async Task Ring_message_has_no_notification_block()
    {
        var (spec, sentAt) = await RingSpecAsync();
        var message = FcmService.ToMessage(spec, sentAt);

        Assert.Null(message.Notification);
        Assert.Null(message.Android.Notification);
        Assert.Equal(Priority.High, message.Android.Priority);
        Assert.Equal("tok-1", message.Token);
    }

    [Fact]
    public async Task Ring_ttl_is_the_time_left()
    {
        var (spec, sentAt) = await RingSpecAsync();

        Assert.Equal(TimeSpan.FromSeconds(Sample("push_ring.json").GetProperty("android").GetProperty("ttlSeconds").GetInt32()),
            FcmService.ToMessage(spec, sentAt).Android.TimeToLive);
        Assert.Equal(TimeSpan.FromSeconds(10), FcmService.ToMessage(spec, Utc(spec.Data["expiresAt"]).AddSeconds(-10.4)).Android.TimeToLive);
        Assert.Equal(TimeSpan.FromSeconds(1), FcmService.ToMessage(spec, Utc(spec.Data["expiresAt"]).AddMilliseconds(-300)).Android.TimeToLive);
        Assert.Equal(TimeSpan.FromSeconds(1), FcmService.ToMessage(spec, Utc(spec.Data["expiresAt"]).AddSeconds(5)).Android.TimeToLive);
        Assert.Equal(TimeSpan.FromSeconds(300), FcmService.ToMessage(spec, Utc(spec.Data["expiresAt"]).AddMinutes(-20)).Android.TimeToLive);
    }

    [Fact]
    public async Task Ring_ttl_follows_the_payload_expiry()
    {
        var data = DataOf(Sample("push_ring.json"));
        var sentAt = Utc(data["sentAt"]);
        var payload = new OutboxPushPayload("ring:x", Driver, null, "t", "b", null, null, "tag", 0,
            NotificationTypes.TaxiRequestRing, sentAt.AddSeconds(42), data);

        var spec = await SpecAsync(payload, sentAt);

        Assert.Equal(TimeSpan.FromSeconds(42), FcmService.ToMessage(spec, sentAt).Android.TimeToLive);
    }

    [Fact]
    public async Task Ring_test_kind_uses_the_ring_shape()
    {
        var data = DataOf(Sample("push_ring.json"));
        var sentAt = Utc(data["sentAt"]);
        data["kind"] = TaxiPushKinds.RingTest;
        data["offerId"] = "";
        var payload = new OutboxPushPayload("ring:test", Driver, null, "t", "b", null, null, "tag", 0,
            NotificationTypes.TaxiRequestRing, sentAt.AddSeconds(30), data);

        var message = FcmService.ToMessage(await SpecAsync(payload, sentAt), sentAt);

        Assert.Null(message.Notification);
        Assert.Equal(TimeSpan.FromSeconds(30), message.Android.TimeToLive);
        Assert.Equal(TaxiPushKinds.RingTest, message.Data["kind"]);
    }

    [Fact]
    public async Task Ring_has_no_collapse_key()
    {
        var (spec, sentAt) = await RingSpecAsync();
        var message = FcmService.ToMessage(spec, sentAt);

        Assert.Null(message.Android.CollapseKey);
        Assert.Null(message.Apns);
        Assert.Null(message.Webpush);
    }

    [Fact]
    public async Task Ring_data_equals_the_contract_sample()
    {
        var (spec, sentAt) = await RingSpecAsync();
        var message = FcmService.ToMessage(spec, sentAt);

        var expected = DataOf(Sample("push_ring.json"));
        Assert.Equal(expected.OrderBy(kv => kv.Key, StringComparer.Ordinal), message.Data.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        Assert.All(message.Data.Values, Assert.NotNull);
    }

    [Fact]
    public async Task Ring_end_is_visible_and_carries_the_ring_tag()
    {
        var sample = Sample("push_ring_end.json");
        var android = sample.GetProperty("android");
        var now = new DateTime(2026, 12, 10, 9, 43, 0, DateTimeKind.Utc);

        var message = FcmService.ToMessage(await RingEndSpecAsync(now), now);

        Assert.Equal(sample.GetProperty("notification").GetProperty("title").GetString(), message.Notification.Title);
        Assert.Equal(sample.GetProperty("notification").GetProperty("body").GetString(), message.Notification.Body);
        Assert.Equal(android.GetProperty("tag").GetString(), message.Android.Notification.Tag);
        Assert.Equal(TaxiPushKinds.RingTag(OfferId), message.Android.Notification.Tag);
        Assert.Equal(android.GetProperty("channelId").GetString(), message.Android.Notification.ChannelId);
        Assert.Equal(TimeSpan.FromSeconds(android.GetProperty("ttlSeconds").GetInt32()), message.Android.TimeToLive);
        Assert.Equal(Priority.High, message.Android.Priority);
        Assert.Null(message.Android.CollapseKey);
        Assert.Equal(DataOf(sample).OrderBy(kv => kv.Key, StringComparer.Ordinal), message.Data.OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }

    // Serialised with the SDK's own JSON attributes; the golden text is today's shape (general_notifications, no TTL, key in data).
    private const string LegacyGolden =
        """{"token":"tok-1","data":{"a":"b","notification_type":"trip_reminder","key":"k1","action_route":"/trip-plan","action_args_json":"{}"},"notification":{"title":"T","body":"B"},"android":{"notification":{"sound":"default","tag":"tag-1","click_action":"FLUTTER_NOTIFICATION_CLICK","channel_id":"general_notifications","sticky":false,"local_only":false,"default_vibrate_timings":false,"default_sound":false,"default_light_settings":false,"event_time":"0001-01-01T00:00:00.000000000Z"},"priority":"high"}}""";

    [Fact]
    public async Task Legacy_push_shape_is_unchanged()
    {
        var now = new DateTime(2026, 12, 10, 9, 43, 0, DateTimeKind.Utc);
        var payload = new OutboxPushPayload("k1", Driver, null, "T", "B", "/trip-plan", "{}", "tag-1", 0, null, null,
            new Dictionary<string, string> { ["a"] = "b" });

        var spec = await SpecAsync(payload, now);
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(FcmService.ToMessage(spec, now),
            new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });

        Assert.Equal(LegacyGolden, json);
        Assert.Equal(json, Newtonsoft.Json.JsonConvert.SerializeObject(FcmService.ToMessage(spec),
            new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore }));
    }
}

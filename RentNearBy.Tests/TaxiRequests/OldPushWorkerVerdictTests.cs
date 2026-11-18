using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.TaxiRequests;

public class OldPushWorkerVerdictTests
{
    private static readonly Guid User = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid NotificationId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");
    private static readonly OldPushNotification Row = new(NotificationId, NotificationTypes.TripReminder, "Title", "Body", "/trip-plan", "{\"tripId\":\"x\"}");

    private sealed class FakeStore(OldPushNotification? row, string[] tokens, bool throwOnLoad = false) : IOldPushStore
    {
        public List<string> Invalid { get; } = [];

        public Task<OldPushNotification?> GetNotificationAsync(Guid notificationId, CancellationToken ct)
            => throwOnLoad ? throw new InvalidOperationException("db down") : Task.FromResult(row);

        public Task<IReadOnlyList<string>> GetTokensAsync(Guid userId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(tokens);

        public Task MarkInvalidAsync(IReadOnlyCollection<string> t, CancellationToken ct)
        {
            Invalid.AddRange(t);
            return Task.CompletedTask;
        }
    }

    // Per-token behaviour: "ok" = sent, "dead" = rejected token, anything else throws.
    private sealed class FakeFcm : IFcmService
    {
        public List<string> Calls { get; } = [];

        public Task<bool> SendAsync(string token, string title, string body, string notificationType, IDictionary<string, string>? data = null)
        {
            lock (Calls) Calls.Add(token);
            return token.StartsWith("ok") ? Task.FromResult(true)
                : token.StartsWith("dead") ? Task.FromResult(false)
                : throw new HttpRequestException("fcm unavailable");
        }

        public Task<IReadOnlyList<FcmSendOutcome>> SendEachAsync(IReadOnlyList<FcmMessageSpec> messages, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SendAsync(string token, string title, string body, string notificationType, NotificationDestination destination) => throw new NotSupportedException();
        public Task<bool> SendToTopicAsync(string topic, string title, string body, IDictionary<string, string>? data = null) => throw new NotSupportedException();
    }

    private sealed class FakePublisher(bool confirm = true) : IConfirmedPublisher
    {
        public List<ConfirmedMessage> Published { get; } = [];

        public Task<IReadOnlyList<PublishResult>> PublishBatchAsync(IReadOnlyList<ConfirmedMessage> messages, CancellationToken ct = default)
        {
            Published.AddRange(messages);
            return Task.FromResult<IReadOnlyList<PublishResult>>(messages.Select(_ => new PublishResult(confirm, confirm ? null : "nack")).ToList());
        }
    }

    private static Task<OldPushAck> Handle(FakeStore store, FakeFcm fcm, FakePublisher publisher)
        => new OldPushHandler(store, fcm, publisher, NullLogger.Instance).HandleAsync(new NotificationPushPayload(NotificationId, User), CancellationToken.None);

    [Fact]
    public void Old_worker_exception_is_not_success()
    {
        Assert.Equal(OldPushOutcome.HandOver, OldPushVerdict.Decide([FcmSendOutcome.Failed]));
        Assert.Equal(OldPushOutcome.HandOver, OldPushVerdict.Decide([FcmSendOutcome.Failed, FcmSendOutcome.InvalidToken]));
        Assert.Equal(OldPushOutcome.Delivered, OldPushVerdict.Decide([FcmSendOutcome.Failed, FcmSendOutcome.Sent]));
        Assert.Equal(OldPushOutcome.NoLiveToken, OldPushVerdict.Decide([FcmSendOutcome.InvalidToken]));
        Assert.Equal(OldPushOutcome.NoLiveToken, OldPushVerdict.Decide([]));
    }

    [Fact]
    public async Task Old_worker_hands_transient_failures_to_the_new_pipeline()
    {
        var store = new FakeStore(Row, ["boom1", "dead1"]);
        var publisher = new FakePublisher();

        var ack = await Handle(store, new FakeFcm(), publisher);

        Assert.Equal(OldPushAck.Ack, ack);
        Assert.Equal(["dead1"], store.Invalid);
        var message = Assert.Single(publisher.Published);
        Assert.Equal("rnb.push", message.Exchange);
        Assert.Equal("send", message.RoutingKey);
        var payload = JsonSerializer.Deserialize<OutboxPushPayload>(message.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(NotificationId, payload.NotificationId);
        Assert.Equal(User, payload.UserId);
        Assert.Equal("Title", payload.Title);
        Assert.Equal("Body", payload.Body);
        Assert.Equal("/trip-plan", payload.Route);
        Assert.Equal("{\"tripId\":\"x\"}", payload.ArgsJson);
        Assert.Null(payload.Kind);
        Assert.Null(payload.ExpiresAtUtc);
        Assert.Equal(payload.Key, message.MessageId);
    }

    [Fact]
    public async Task An_unconfirmed_hand_over_is_requeued()
        => Assert.Equal(OldPushAck.Requeue, await Handle(new FakeStore(Row, ["boom"]), new FakeFcm(), new FakePublisher(confirm: false)));

    [Fact]
    public async Task A_load_failure_is_requeued_not_acked()
    {
        var publisher = new FakePublisher();
        Assert.Equal(OldPushAck.Requeue, await Handle(new FakeStore(Row, ["ok"], throwOnLoad: true), new FakeFcm(), publisher));
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task Delivered_dead_only_and_gone_rows_are_acked_without_hand_over()
    {
        var publisher = new FakePublisher();
        Assert.Equal(OldPushAck.Ack, await Handle(new FakeStore(Row, ["ok1", "boom"]), new FakeFcm(), publisher));
        Assert.Equal(OldPushAck.Ack, await Handle(new FakeStore(Row, ["dead1"]), new FakeFcm(), publisher));
        Assert.Equal(OldPushAck.Ack, await Handle(new FakeStore(null, ["ok1"]), new FakeFcm(), publisher));
        Assert.Equal(OldPushAck.Ack, await Handle(new FakeStore(Row, []), new FakeFcm(), publisher));
        Assert.Empty(publisher.Published);
    }
}

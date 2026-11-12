using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

public class PushRetryLadderTests
{
    private static readonly Guid U1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid U2 = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Theory]
    [InlineData(1, "retry.30s")]
    [InlineData(2, "retry.5m")]
    [InlineData(3, "dead")]
    [InlineData(9, "dead")]
    public void Ladder_is_30_seconds_then_5_minutes_then_dead(int failure, string route)
        => Assert.Equal(route, RabbitTopology.NextRoute(failure));

    [Fact]
    public void Topology_names_are_the_additive_set_and_not_the_old_queue()
    {
        Assert.Equal("rnb.push", RabbitTopology.Exchange);
        Assert.Equal("rnb.push.dlx", RabbitTopology.DeadExchange);
        Assert.Equal(["push.send", "push.retry.30s", "push.retry.5m", "push.dead"],
            new[] { RabbitTopology.SendQueue, RabbitTopology.Retry30Queue, RabbitTopology.Retry5mQueue, RabbitTopology.DeadQueue });
        Assert.Equal(30_000, RabbitTopology.Retry30Ms);
        Assert.Equal(300_000, RabbitTopology.Retry5mMs);
        Assert.DoesNotContain("notification.push", new[] { RabbitTopology.SendQueue, RabbitTopology.Retry30Queue, RabbitTopology.Retry5mQueue, RabbitTopology.DeadQueue });
    }

    private sealed class FakeStore(Dictionary<Guid, string[]> tokens, Dictionary<Guid, string>? types = null) : IPushDeliveryStore
    {
        public List<string> Invalid { get; } = [];
        public int TokenQueries { get; private set; }
        public int TypeQueries { get; private set; }

        public Task<IReadOnlyList<(Guid UserId, string Token)>> GetTokensAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
        {
            TokenQueries++;
            return Task.FromResult<IReadOnlyList<(Guid, string)>>(tokens.Where(k => userIds.Contains(k.Key)).SelectMany(k => k.Value.Select(t => (k.Key, t))).ToList());
        }

        public Task<IReadOnlyDictionary<Guid, string>> GetNotificationTypesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
        {
            TypeQueries++;
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>((types ?? []).Where(k => ids.Contains(k.Key)).ToDictionary(k => k.Key, k => k.Value));
        }

        public Task MarkInvalidAsync(IReadOnlyCollection<string> t, CancellationToken ct)
        {
            Invalid.AddRange(t);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFcm(Func<FcmMessageSpec, FcmSendOutcome> decide) : IFcmService
    {
        public List<FcmMessageSpec> Sent { get; } = [];
        public int Calls { get; private set; }

        public Task<IReadOnlyList<FcmSendOutcome>> SendEachAsync(IReadOnlyList<FcmMessageSpec> messages, CancellationToken ct = default)
        {
            Calls++;
            Sent.AddRange(messages);
            return Task.FromResult<IReadOnlyList<FcmSendOutcome>>(messages.Select(decide).ToList());
        }

        public Task<bool> SendAsync(string token, string title, string body, string notificationType, IDictionary<string, string>? data = null) => throw new NotSupportedException();
        public Task<bool> SendAsync(string token, string title, string body, string notificationType, NotificationDestination destination) => throw new NotSupportedException();
        public Task<bool> SendToTopicAsync(string topic, string title, string body, IDictionary<string, string>? data = null) => throw new NotSupportedException();
    }

    private static PushDelivery Delivery(Guid user, ulong tag, Guid? notificationId = null, int attempt = 0)
        => new(tag, new OutboxPushPayload($"k{tag}", user, notificationId, "T", "B", "/trip-plan", "{\"tripId\":\"x\"}", $"tag{tag}"), attempt);

    private static Task<IReadOnlyList<PushVerdict>> Run(IReadOnlyList<PushDelivery> batch, FakeStore store, FakeFcm fcm)
        => PushBatchProcessor.ProcessAsync(batch, store, fcm, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task One_batch_uses_one_token_query_and_one_fcm_call_for_many_users()
    {
        var store = new FakeStore(new() { [U1] = ["a"], [U2] = ["b", "c"] });
        var fcm = new FakeFcm(_ => FcmSendOutcome.Sent);
        var verdicts = await Run([Delivery(U1, 1), Delivery(U2, 2)], store, fcm);

        Assert.All(verdicts, v => Assert.Equal(PushVerdict.Ack, v));
        Assert.Equal(1, store.TokenQueries);
        Assert.Equal(1, fcm.Calls);
        Assert.Equal(3, fcm.Sent.Count);
    }

    [Fact]
    public async Task Fcm_data_carries_route_args_type_and_a_replace_tag()
    {
        var store = new FakeStore(new() { [U1] = ["a"] });
        var fcm = new FakeFcm(_ => FcmSendOutcome.Sent);
        await Run([Delivery(U1, 7)], store, fcm);

        var spec = Assert.Single(fcm.Sent);
        Assert.Equal("/trip-plan", spec.Data["action_route"]);
        Assert.Equal("{\"tripId\":\"x\"}", spec.Data["action_args_json"]);
        Assert.Equal("trip_reminder", spec.Data["notification_type"]);
        Assert.Equal("tag7", spec.Tag);
    }

    [Fact]
    public async Task A_push_with_an_inbox_row_uses_the_row_type_and_skips_when_the_row_is_gone()
    {
        var present = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var store = new FakeStore(new() { [U1] = ["a"] }, new() { [present] = NotificationTypes.TripEnquiryReceived });
        var fcm = new FakeFcm(_ => FcmSendOutcome.Sent);
        var verdicts = await Run([Delivery(U1, 1, present), Delivery(U1, 2, gone)], store, fcm);

        Assert.Equal([PushVerdict.Ack, PushVerdict.Ack], verdicts);
        Assert.Equal(1, store.TypeQueries);
        var spec = Assert.Single(fcm.Sent);
        Assert.Equal("trip_enquiry_received", spec.Data["notification_type"]);
    }

    [Fact]
    public async Task A_user_without_tokens_is_acked_and_never_sent()
    {
        var store = new FakeStore([]);
        var fcm = new FakeFcm(_ => FcmSendOutcome.Sent);
        var verdicts = await Run([Delivery(U1, 1)], store, fcm);

        Assert.Equal([PushVerdict.Ack], verdicts);
        Assert.Equal(0, fcm.Calls);
    }

    [Fact]
    public async Task Delivered_when_any_device_took_it_so_three_devices_never_duplicate()
    {
        var store = new FakeStore(new() { [U1] = ["a", "b", "c"] });
        var fcm = new FakeFcm(s => s.Token == "a" ? FcmSendOutcome.Sent : FcmSendOutcome.Failed);
        var verdicts = await Run([Delivery(U1, 1)], store, fcm);
        Assert.Equal([PushVerdict.Ack], verdicts);
    }

    [Fact]
    public async Task All_transient_failures_retry_and_never_mark_tokens_invalid()
    {
        var store = new FakeStore(new() { [U1] = ["a", "b"] });
        var fcm = new FakeFcm(_ => FcmSendOutcome.Failed);
        var verdicts = await Run([Delivery(U1, 1)], store, fcm);

        Assert.Equal([PushVerdict.Retry], verdicts);
        Assert.Empty(store.Invalid);
    }

    [Fact]
    public async Task Only_unregistered_tokens_are_marked_invalid_in_one_call_and_the_push_is_not_retried()
    {
        var store = new FakeStore(new() { [U1] = ["dead1"], [U2] = ["dead2", "ok"] });
        var fcm = new FakeFcm(s => s.Token.StartsWith("dead") ? FcmSendOutcome.InvalidToken : FcmSendOutcome.Sent);
        var verdicts = await Run([Delivery(U1, 1), Delivery(U2, 2)], store, fcm);

        Assert.Equal([PushVerdict.Ack, PushVerdict.Ack], verdicts);
        Assert.Equal(["dead1", "dead2"], store.Invalid.Order());
    }

    [Fact]
    public async Task An_unreadable_message_goes_straight_to_dead()
    {
        var store = new FakeStore([]);
        var fcm = new FakeFcm(_ => FcmSendOutcome.Sent);
        var verdicts = await Run([new PushDelivery(1, null, 0)], store, fcm);
        Assert.Equal([PushVerdict.Dead], verdicts);
        Assert.Equal(0, store.TokenQueries);
    }

    [Fact]
    public async Task A_whole_call_failure_retries_every_message()
    {
        var store = new FakeStore(new() { [U1] = ["a"] });
        var fcm = new ThrowingFcm();
        var verdicts = await PushBatchProcessor.ProcessAsync([Delivery(U1, 1)], store, fcm, NullLogger.Instance, CancellationToken.None);
        Assert.Equal([PushVerdict.Retry], verdicts);
    }

    private sealed class ThrowingFcm : IFcmService
    {
        public Task<IReadOnlyList<FcmSendOutcome>> SendEachAsync(IReadOnlyList<FcmMessageSpec> messages, CancellationToken ct = default) => throw new InvalidOperationException("fcm down");
        public Task<bool> SendAsync(string token, string title, string body, string notificationType, IDictionary<string, string>? data = null) => throw new NotSupportedException();
        public Task<bool> SendAsync(string token, string title, string body, string notificationType, NotificationDestination destination) => throw new NotSupportedException();
        public Task<bool> SendToTopicAsync(string topic, string title, string body, IDictionary<string, string>? data = null) => throw new NotSupportedException();
    }

    private static string? BackendFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        return dir == null ? null : Path.Combine([dir.FullName, .. parts]);
    }

    [Fact]
    public void The_old_notification_push_queue_keeps_null_arguments()
    {
        var path = BackendFile("RentNearBy.Infrastructure", "Services", "NotificationPushWorkerService.cs");
        if (path == null) return;
        var text = File.ReadAllText(path);
        Assert.Contains("queue: QueueName", text);
        Assert.Contains("arguments: null", text);
        Assert.Contains("\"notification.push\"", text);
    }

    [Fact]
    public void The_timer_no_longer_pushes_expiry_or_24h_no_reply()
    {
        var path = BackendFile("RentNearBy.Infrastructure", "Services", "TripTimerService.cs");
        if (path == null) return;
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("$\"exp:", text);
        Assert.DoesNotContain("NoReplyKey", text);
        Assert.DoesNotContain("NoReplySegmentKey", text);
        Assert.DoesNotContain("GetNoReplyDueAsync", text);
        Assert.DoesNotContain("TripEnquiryExpired,", text);
    }
}

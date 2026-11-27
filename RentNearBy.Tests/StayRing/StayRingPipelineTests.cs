using System.Text.Json;
using FirebaseAdmin.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Models.StayRing;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.StayRing;

namespace RentNearBy.Tests.StayRing;

// Stay ring through the real push pipeline (PushBatchProcessor + FcmService) and the outbox rows the writer adds.
public class StayRingPipelineTests
{
    private static readonly Guid Owner = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Enquiry = Guid.Parse("5a5a5a5a-0001-4a00-8000-000000000001");
    private static readonly DateTime Now = new(2026, 12, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class FakeStore(HashSet<Guid>? endedStay = null, bool stayAware = true) : IPushDeliveryStore
    {
        public readonly List<Guid[]> StayQueries = [];
        public readonly List<Guid[]> OfferQueries = [];

        public Task<IReadOnlyList<(Guid UserId, string Token)>> GetTokensAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(Guid, string)>>(userIds.Select(u => (u, "tok-" + u.ToString("N")[..4])).ToList());

        public Task<IReadOnlyDictionary<Guid, string>> GetNotificationTypesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task MarkInvalidAsync(IReadOnlyCollection<string> t, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlySet<Guid>> GetEndedRingOffersAsync(IReadOnlyCollection<Guid> offerIds, CancellationToken ct)
        {
            OfferQueries.Add(offerIds.ToArray());
            return Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        }

        public Task<IReadOnlySet<Guid>> GetSeekersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlySet<Guid>> GetEndedStayRingsAsync(IReadOnlyCollection<Guid> enquiryIds, CancellationToken ct)
        {
            if (!stayAware) throw new InvalidOperationException("not expected");
            StayQueries.Add(enquiryIds.ToArray());
            return Task.FromResult<IReadOnlySet<Guid>>(enquiryIds.Where(id => endedStay?.Contains(id) == true).ToHashSet());
        }
    }

    // A store written before the stay ring (no override) keeps working: the default says nothing ended.
    private sealed class OldStore : IPushDeliveryStore
    {
        public Task<IReadOnlyList<(Guid UserId, string Token)>> GetTokensAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(Guid, string)>>(userIds.Select(u => (u, "tok")).ToList());
        public Task<IReadOnlyDictionary<Guid, string>> GetNotificationTypesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
        public Task MarkInvalidAsync(IReadOnlyCollection<string> t, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlySet<Guid>> GetEndedRingOffersAsync(IReadOnlyCollection<Guid> offerIds, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlySet<Guid>> GetSeekersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
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

    private static StayRingSnapshot Snapshot() => new(
        Enquiry, "Priya", "Pine View Stay", new DateOnly(2026, 12, 18), new DateOnly(2026, 12, 20), 2, 0, 1, null, TripOwnerBuckets.New);

    private static OutboxPushPayload RingPayload(DateTime? expires = null)
    {
        var at = expires ?? Now.AddSeconds(300);
        var data = StayRingPayload.Build(Snapshot(), at, Now.AddSeconds(-5), Owner);
        return new OutboxPushPayload(StayRingPushKinds.RingKey(Enquiry), Owner, null, StayRingPayload.RingTitle, data["line1"], data["action_route"],
            data["action_args_json"], StayRingPushKinds.RingTag(Enquiry), 0, NotificationTypes.StayEnquiryRing, at, data);
    }

    private static OutboxPushPayload MissedPayload()
    {
        var m = StayRingPayload.BuildMissed(Snapshot(), Owner);
        return new OutboxPushPayload(StayRingPushKinds.MissedKey(Enquiry), Owner, null, m.Title, m.Body, m.Data["action_route"], m.Data["action_args_json"],
            StayRingPushKinds.RingTag(Enquiry), 0, NotificationTypes.StayEnquiryRingMissed, Now.AddHours(20), m.Data);
    }

    private static OutboxPushPayload EndPayload(string reason = StayRingEndReasons.Answered, string? except = null)
    {
        var e = StayRingPayload.BuildEnd(Enquiry, reason, Owner);
        return new OutboxPushPayload(StayRingPushKinds.RingEndKey(Enquiry), Owner, null, e.Title, e.Body, e.Data["action_route"], e.Data["action_args_json"],
            StayRingPushKinds.RingTag(Enquiry), 0, NotificationTypes.StayEnquiryRingEnded, Now.AddSeconds(60), e.Data, except);
    }

    private static Task<IReadOnlyList<PushVerdict>> Run(IPushDeliveryStore store, FakeFcm fcm, params OutboxPushPayload[] payloads)
        => PushBatchProcessor.ProcessAsync(payloads.Select((p, i) => new PushDelivery((ulong)i + 1, p, 0)).ToList(), store, fcm, NullLogger.Instance, Now, CancellationToken.None);

    // Use case 1 delivery + use case 10: data-only, high priority, TTL = seconds left, no key, sentAt = this send.
    [Fact]
    public async Task Stay_ring_is_sent_as_the_taxi_ring_shape()
    {
        var fcm = new FakeFcm();
        var store = new FakeStore();
        Assert.Equal([PushVerdict.Ack], await Run(store, fcm, RingPayload()));
        var spec = Assert.Single(fcm.Sent);
        Assert.False(spec.Data.ContainsKey("key"));
        Assert.Equal("2026-12-01T10:00:00Z", spec.Data["sentAt"]);
        Assert.Equal("2026-12-01T10:05:00Z", spec.Data["expiresAt"]);
        Assert.Equal("stay_enquiry_ring", spec.Data["notification_type"]);
        Assert.Equal([Enquiry], Assert.Single(store.StayQueries));
        Assert.Empty(store.OfferQueries);

        var message = FcmService.ToMessage(spec, Now);
        Assert.Null(message.Notification);
        Assert.Equal(Priority.High, message.Android.Priority);
        Assert.Equal(TimeSpan.FromSeconds(300), message.Android.TimeToLive);
        Assert.Null(message.Android.CollapseKey);
        Assert.Null(message.Android.Notification);
    }

    // Use cases 7, 8: a ring (or missed ring) of an enquiry that was answered or closed is acked, never sent.
    [Fact]
    public async Task Ring_and_missed_ring_of_an_ended_enquiry_are_dropped()
    {
        var fcm = new FakeFcm();
        var verdicts = await Run(new FakeStore([Enquiry]), fcm, RingPayload(), MissedPayload());
        Assert.Equal([PushVerdict.Ack, PushVerdict.Ack], verdicts);
        Assert.Empty(fcm.Sent);
    }

    [Fact]
    public async Task Ring_past_its_window_is_never_sent()
    {
        var fcm = new FakeFcm();
        Assert.Equal([PushVerdict.Ack], await Run(new FakeStore(), fcm, RingPayload(Now.AddSeconds(-1))));
        Assert.Empty(fcm.Sent);
    }

    // Use case 9: visible, the ring tag (replaces the ringing notification), stay channel, enquiry route.
    [Fact]
    public async Task Missed_ring_is_visible_with_the_ring_tag()
    {
        var fcm = new FakeFcm();
        await Run(new FakeStore(), fcm, MissedPayload());
        var spec = Assert.Single(fcm.Sent);
        Assert.False(spec.Data.ContainsKey("key"));
        var message = FcmService.ToMessage(spec, Now);
        Assert.Equal(StayRingPayload.MissedTitle, message.Notification.Title);
        Assert.Equal(StayRingPushKinds.RingTag(Enquiry), message.Android.Notification.Tag);
        Assert.Equal(StayRingPushKinds.UpdatesChannelId, message.Android.Notification.ChannelId);
        Assert.Equal(TimeSpan.FromSeconds(StayRingRules.MissedTtlSeconds), message.Android.TimeToLive);
        Assert.Equal(UserNotificationRoutes.StayEnquiries, message.Data["action_route"]);
    }

    // Use case 7: ring end is visible with the ring tag, TTL 60 s, never to the answering device.
    [Fact]
    public async Task Ring_end_is_visible_and_skips_the_answering_device()
    {
        var fcm = new FakeFcm();
        var store = new FakeStore();
        var token = "tok-" + Owner.ToString("N")[..4];
        await Run(store, fcm, EndPayload(StayRingEndReasons.Accepted, except: token));
        Assert.Empty(fcm.Sent);
        Assert.Empty(store.StayQueries);

        await Run(store, fcm, EndPayload());
        var spec = Assert.Single(fcm.Sent);
        Assert.False(spec.Data.ContainsKey("key"));
        var message = FcmService.ToMessage(spec, Now);
        Assert.Equal(StayRingEndReasons.Body(StayRingEndReasons.Answered), message.Notification.Body);
        Assert.Equal(StayRingPushKinds.RingTag(Enquiry), message.Android.Notification.Tag);
        Assert.Equal(StayRingPushKinds.UpdatesChannelId, message.Android.Notification.ChannelId);
        Assert.Equal(TimeSpan.FromSeconds(60), message.Android.TimeToLive);
    }

    [Fact]
    public async Task Test_ring_has_no_enquiry_to_check_and_is_sent()
    {
        var fcm = new FakeFcm();
        var store = new FakeStore(stayAware: false);
        var row = StayRingTestRing.Outbox(Owner, Now);
        var payload = JsonSerializer.Deserialize<OutboxPushPayload>(row.Payload, Json)!;
        Assert.Null(PushBatchProcessor.StayRingEnquiryId(payload));
        await Run(store, fcm, payload);
        var spec = Assert.Single(fcm.Sent);
        Assert.Equal(StayRingPushKinds.RingTest, spec.Data["kind"]);
        Assert.Equal(Now.AddSeconds(30), payload.ExpiresAtUtc);
        Assert.StartsWith(StayRingPushKinds.TestKeyPrefix, row.Key);
    }

    // Use case 13: taxi pushes never reach the stay check and keep their shape; an old store keeps compiling and working.
    [Fact]
    public async Task Taxi_ring_never_asks_the_stay_check_and_an_old_store_still_works()
    {
        var data = new Dictionary<string, string> { ["offerId"] = Guid.NewGuid().ToString("D"), ["expiresAt"] = "x", ["key"] = "k" };
        var taxi = new OutboxPushPayload("ring:1", Owner, null, "New taxi request", "l1", "/taxi-request-ring", "{}", "taxi_ring_1", 0,
            NotificationTypes.TaxiRequestRing, Now.AddSeconds(200), data);
        var fcm = new FakeFcm();
        var store = new FakeStore(stayAware: false);
        await Run(store, fcm, taxi);
        var spec = Assert.Single(fcm.Sent);
        Assert.False(spec.Data.ContainsKey("key"));
        Assert.Equal("2026-12-01T10:03:20Z", spec.Data["expiresAt"]);
        Assert.Single(store.OfferQueries);
        Assert.Null(PushBatchProcessor.StayRingEnquiryId(taxi));
        Assert.Equal("taxi_request_updates_v1", TaxiPushKinds.UpdatesChannelId);

        var old = new FakeFcm();
        Assert.Equal([PushVerdict.Ack], await Run(new OldStore(), old, RingPayload()));
        Assert.Single(old.Sent);
    }

    [Fact]
    public void Stay_ring_enquiry_id_is_read_only_from_ring_and_missed_rows()
    {
        Assert.Equal(Enquiry, PushBatchProcessor.StayRingEnquiryId(RingPayload()));
        Assert.Equal(Enquiry, PushBatchProcessor.StayRingEnquiryId(MissedPayload()));
        Assert.Null(PushBatchProcessor.StayRingEnquiryId(EndPayload()));
        Assert.Null(PushBatchProcessor.StayRingEnquiryId(RingPayload() with { NotificationId = Guid.NewGuid() }));
    }

    // ---- writer rows (no database: rows are only added to the context) ----

    private static ApplicationDbContext DesignContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite()).Options);

    private static TripEnquiry Row(DateTime expiresAt) => new()
    {
        Id = Enquiry, Kind = TripEnquiryKinds.Stay, OwnerUserId = Owner, ListingId = Guid.NewGuid(), ListingName = "Pine View Stay",
        CheckIn = new DateOnly(2026, 12, 18), CheckOut = new DateOnly(2026, 12, 20), Status = TripEnquiryStatuses.Asked, ExpiresAt = expiresAt,
        ListedStayTotal = 6000, CreatedAt = Now, UpdatedAt = Now,
    };

    private static OutboxPushPayload PayloadOf(OutboxMessage m) => JsonSerializer.Deserialize<OutboxPushPayload>(m.Payload, Json)!;

    // Use cases 1, 9: ring row due now (expires with the window), missed row due at the window's end; the enquiry is not touched.
    [Fact]
    public void Writer_adds_the_ring_and_a_missed_ring_due_at_the_window_end()
    {
        using var db = DesignContext();
        var row = Row(Now.AddHours(24));
        StayRingWriter.AddRing(db, new StayRingRequest(row, "Priya Sharma", 2, 1, 1), Owner, Now);

        var added = db.ChangeTracker.Entries<OutboxMessage>().Select(e => e.Entity).OrderBy(m => m.Key, StringComparer.Ordinal).ToList();
        Assert.Equal([StayRingPushKinds.RingKey(Enquiry), StayRingPushKinds.MissedKey(Enquiry)], added.Select(m => m.Key));
        var ring = added[0];
        var missed = added[1];
        Assert.Equal(Now, ring.NextAttemptAt);
        Assert.Equal(Now.AddSeconds(300), missed.NextAttemptAt);
        Assert.All(added, m => Assert.Equal(OutboxTargets.PushExchange, m.Exchange));

        var ringPush = PayloadOf(ring);
        Assert.Equal(NotificationTypes.StayEnquiryRing, ringPush.Kind);
        Assert.Null(ringPush.NotificationId);
        Assert.Equal(Owner, ringPush.UserId);
        Assert.Equal(Now.AddSeconds(300), ringPush.ExpiresAtUtc);
        Assert.Equal("6000", ringPush.Data!["price"]);
        Assert.Equal("Priya", ringPush.Data["guestName"]);
        var missedPush = PayloadOf(missed);
        Assert.Equal(NotificationTypes.StayEnquiryRingMissed, missedPush.Kind);
        Assert.Equal(row.ExpiresAt, missedPush.ExpiresAtUtc);
        Assert.Equal(StayRingPushKinds.RingTag(Enquiry), missedPush.Tag);

        Assert.Empty(db.ChangeTracker.Entries<TripEnquiry>());
        Assert.Equal(TripEnquiryStatuses.Asked, row.Status);
        Assert.Equal(Now, row.UpdatedAt);
    }

    [Fact]
    public void An_enquiry_that_ends_inside_the_window_gets_no_missed_ring()
    {
        using var db = DesignContext();
        StayRingWriter.AddRing(db, new StayRingRequest(Row(Now.AddSeconds(120)), null, null, null, null), Owner, Now);
        var only = Assert.Single(db.ChangeTracker.Entries<OutboxMessage>()).Entity;
        Assert.Equal(StayRingPushKinds.RingKey(Enquiry), only.Key);
        Assert.Equal(Now.AddSeconds(120), PayloadOf(only).ExpiresAtUtc);
    }

    [Fact]
    public void Ring_end_row_carries_the_answering_token_and_the_tag()
    {
        using var db = DesignContext();
        StayRingWriter.AddRingEnd(db, StayRingPushKinds.RingEndKey(Enquiry), Enquiry, Owner, StayRingEndReasons.Declined, Now, "tok-a");
        var push = PayloadOf(Assert.Single(db.ChangeTracker.Entries<OutboxMessage>()).Entity);
        Assert.Equal(NotificationTypes.StayEnquiryRingEnded, push.Kind);
        Assert.Equal("tok-a", push.ExceptToken);
        Assert.Equal(StayRingPushKinds.RingTag(Enquiry), push.Tag);
        Assert.Equal(Now.AddSeconds(60), push.ExpiresAtUtc);
        Assert.True(StayRingWriter.IsRingEndKey(push.Key));
    }

    // Use case 16: account deletion removes his unsent pushes by the payload's userId: the missed ring row is one of them.
    [Fact]
    public void Pending_missed_ring_is_covered_by_account_deletion()
    {
        using var db = DesignContext();
        StayRingWriter.AddRing(db, new StayRingRequest(Row(Now.AddHours(24)), null, null, null, null), Owner, Now);
        var missed = db.ChangeTracker.Entries<OutboxMessage>().Single(e => e.Entity.Key.StartsWith(StayRingPushKinds.MissedKeyPrefix)).Entity;
        using var doc = JsonDocument.Parse(missed.Payload);
        Assert.Equal(Owner.ToString(), doc.RootElement.GetProperty(RentNearBy.Infrastructure.Services.Owner.Be4.OwnerNotifier.UserIdProperty).GetString());
        Assert.Contains("\"PublishedAt\" IS NULL", RentNearBy.Infrastructure.Services.Owner.Be4.OwnerAccountDeletion.UnsentPushesSql);
        Assert.Null(missed.PublishedAt);
    }

    // ---- ring end pass (pure parts) ----

    [Fact]
    public void Pass_reads_live_rings_from_missed_rows_and_ends_only_left_asked()
    {
        var other = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(MissedPayload(), Json);
        var live = StayRingEndPass.Parse([
            (StayRingPushKinds.MissedKey(Enquiry), payload),
            (StayRingPushKinds.MissedKey(other), payload),
            (StayRingPushKinds.MissedKey(gone), payload),
            ("stayringmiss:bad", payload),
            (StayRingPushKinds.MissedKey(Guid.NewGuid()), "{not json"),
        ]);
        Assert.Equal(3, live.Count);
        Assert.All(live, r => Assert.Equal(Owner, r.OwnerUserId));

        var ended = StayRingEndPass.Ended(live, new Dictionary<Guid, string>
        {
            [Enquiry] = TripEnquiryStatuses.Offered,
            [other] = TripEnquiryStatuses.Asked,
        });
        Assert.Equal(2, ended.Count);
        Assert.Contains(ended, x => x.Ring.EnquiryId == Enquiry && x.Reason == StayRingEndReasons.Answered);
        Assert.Contains(ended, x => x.Ring.EnquiryId == gone && x.Reason == StayRingEndReasons.Closed);
        Assert.DoesNotContain(ended, x => x.Ring.EnquiryId == other);
    }

    [Fact]
    public void Pass_reads_through_the_due_index_and_cancels_with_one_winner()
    {
        Assert.Contains("\"PublishedAt\" IS NULL AND \"NextAttemptAt\" > now()", StayRingEndPass.LiveRingsSql);
        Assert.Contains("ORDER BY \"NextAttemptAt\", \"Id\"", StayRingEndPass.LiveRingsSql);
        Assert.Contains("'stayringmiss:%'", StayRingEndPass.LiveRingsSql);
        Assert.Contains("AND \"PublishedAt\" IS NULL", StayRingWriter.CancelMissedSql);
        Assert.Contains("RETURNING \"Key\"", StayRingWriter.CancelMissedSql);
        Assert.Contains("\"Payload\"::jsonb ->> 'userId'", StayRingWriter.LiveRingsOfOwnerSql);
        Assert.Equal(TimeSpan.FromSeconds(5), StayRingRules.WatchInterval);
    }
}

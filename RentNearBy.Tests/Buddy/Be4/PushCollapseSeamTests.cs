using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Buddy.Be4;

namespace RentNearBy.Tests.Buddy.Be4;

// Outbox payload -> PushBatchProcessor -> FCM message: only informational buddy pushes carry an FCM collapse key.
public class PushCollapseSeamTests
{
    private static readonly Guid User = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Trip = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private sealed class FakeStore : IPushDeliveryStore
    {
        public Task<IReadOnlyList<(Guid UserId, string Token)>> GetTokensAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(Guid, string)>>([(User, "tok-1")]);

        public Task<IReadOnlyDictionary<Guid, string>> GetNotificationTypesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task MarkInvalidAsync(IReadOnlyCollection<string> t, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlySet<Guid>> GetEndedRingOffersAsync(IReadOnlyCollection<Guid> offerIds, CancellationToken ct)
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

    private static async Task<FcmMessageSpec> SendAsync(PushSpec push)
    {
        var payload = JsonSerializer.Deserialize<OutboxPushPayload>(NoticeWriter.Payload(push), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var fcm = new FakeFcm();
        var verdicts = await PushBatchProcessor.ProcessAsync([new PushDelivery(1, payload, 0)], new FakeStore(), fcm, NullLogger.Instance, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal([PushVerdict.Ack], verdicts);
        return Assert.Single(fcm.Sent);
    }

    [Fact]
    public async Task Expense_update_push_collapses_on_its_tag()
    {
        var push = BuddyPushCopy.TripExpensesUpdated(User, Trip, "Rahul", "T", DateTime.UtcNow);
        var spec = await SendAsync(push);
        Assert.Equal(push.Tag, spec.Tag);
        Assert.Equal(push.Tag, spec.CollapseKey);
        Assert.Equal(push.Tag, FcmService.ToMessage(spec).Android.CollapseKey);
        Assert.Equal("trip_expenses_updated", spec.Data["notification_type"]);
        Assert.Equal(UserNotificationRoutes.TripExpenses, spec.Data["action_route"]);
    }

    [Fact]
    public async Task Reminder_and_pay_request_pushes_are_never_collapsed_by_fcm()
    {
        PushSpec[] pushes =
        [
            BuddyPushCopy.ManualReminder(User, Trip, "T", [("Rahul", 100L)], new DateOnly(2026, 5, 19)),
            BuddyPushCopy.PaymentSaidPaid(User, Guid.NewGuid(), Trip, "Aman", 100, "T", "Rahul", toOwner: true),
            BuddyPushCopy.PaymentNotReceived(User, Guid.NewGuid(), Trip, "Rahul", 100),
        ];
        foreach (var push in pushes)
        {
            var spec = await SendAsync(push);
            Assert.Equal(push.Tag, spec.Tag);
            Assert.Null(spec.CollapseKey);
            Assert.Null(FcmService.ToMessage(spec).Android.CollapseKey);
            Assert.Equal(push.Tag, FcmService.ToMessage(spec).Android.Notification.Tag);
        }
    }
}

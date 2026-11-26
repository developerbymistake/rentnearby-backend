using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.SeekerOnly;

// Contract 4.3 (step B6): a traveller push reaches only an account whose live role is SEEKER; owner and driver pushes are untouched.
public class SeekerPushDeliveryTests
{
    private static readonly Guid Seeker = Guid.Parse("5ee6e000-0000-4000-8000-000000000001");
    private static readonly Guid Owner = Guid.Parse("5ee6e000-0000-4000-8000-000000000002");
    private static readonly DateTime Now = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

    private sealed class FakeStore(IReadOnlyDictionary<Guid, string>? types = null) : IPushDeliveryStore
    {
        public List<Guid[]> SeekerQueries { get; } = [];

        public Task<IReadOnlyList<(Guid UserId, string Token)>> GetTokensAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(Guid, string)>>(userIds.Select(u => (u, $"tok-{u:N}")).ToList());

        public Task<IReadOnlyDictionary<Guid, string>> GetNotificationTypesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<Guid, string>>((types ?? new Dictionary<Guid, string>())
                .Where(k => ids.Contains(k.Key)).ToDictionary(k => k.Key, k => k.Value));

        public Task MarkInvalidAsync(IReadOnlyCollection<string> t, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlySet<Guid>> GetEndedRingOffersAsync(IReadOnlyCollection<Guid> offerIds, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlySet<Guid>> GetSeekersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
        {
            SeekerQueries.Add(userIds.ToArray());
            return Task.FromResult<IReadOnlySet<Guid>>(userIds.Where(u => u == Seeker).ToHashSet());
        }
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

    private static PushDelivery Push(ulong tag, Guid user, string? route, Guid? notificationId = null, string? kind = null, DateTime? expires = null)
        => new(tag, new OutboxPushPayload($"k{tag}", user, notificationId, "T", "B", route, "{}", $"tag{tag}", 0, kind, expires), 0);

    private static Task<IReadOnlyList<PushVerdict>> Run(IReadOnlyList<PushDelivery> batch, FakeStore store, FakeFcm fcm)
        => PushBatchProcessor.ProcessAsync(batch, store, fcm, NullLogger.Instance, Now, CancellationToken.None);

    private static Guid[] SentTo(FakeFcm fcm) => fcm.Sent.Select(s => Guid.ParseExact(s.Token["tok-".Length..], "N")).ToArray();

    [Fact]
    public async Task A_traveller_type_reaches_the_seeker_and_is_acked_unsent_for_an_owner()
    {
        var toSeeker = Guid.NewGuid();
        var toOwner = Guid.NewGuid();
        var store = new FakeStore(new Dictionary<Guid, string>
        {
            [toSeeker] = NotificationTypes.TripOfferReceived,
            [toOwner] = NotificationTypes.TripOfferReceived,
        });
        var fcm = new FakeFcm();

        var verdicts = await Run([Push(1, Seeker, UserNotificationRoutes.ChatConversation, toSeeker), Push(2, Owner, UserNotificationRoutes.ChatConversation, toOwner)], store, fcm);

        Assert.Equal([PushVerdict.Ack, PushVerdict.Ack], verdicts);
        Assert.Equal([Seeker], SentTo(fcm));
        Assert.Equal([Seeker, Owner], Assert.Single(store.SeekerQueries).Order());
    }

    [Fact]
    public async Task A_shared_type_that_opens_a_traveller_page_is_not_sent_to_an_owner()
    {
        var fcm = new FakeFcm();
        var verdicts = await Run([Push(1, Owner, UserNotificationRoutes.TripPlan), Push(2, Seeker, UserNotificationRoutes.TripPlan)], new FakeStore(), fcm);

        Assert.Equal([PushVerdict.Ack, PushVerdict.Ack], verdicts);
        Assert.Equal([Seeker], SentTo(fcm));
    }

    [Fact]
    public async Task Buddy_and_taxi_request_pushes_are_not_sent_to_an_owner()
    {
        var taxiEvent = Guid.NewGuid();
        var store = new FakeStore(new Dictionary<Guid, string> { [taxiEvent] = NotificationTypes.TaxiChooseDriver });
        var fcm = new FakeFcm();

        var verdicts = await Run(
        [
            Push(1, Owner, UserNotificationRoutes.Buddies, kind: NotificationTypes.BuddyRequestReceived),
            Push(2, Owner, UserNotificationRoutes.TripTaxiStatus, taxiEvent),
            Push(3, Owner, UserNotificationRoutes.TripExpenses, kind: NotificationTypes.TripSettleReminder),
        ], store, fcm);

        Assert.All(verdicts, v => Assert.Equal(PushVerdict.Ack, v));
        Assert.Empty(fcm.Sent);
    }

    [Fact]
    public async Task Host_and_driver_pushes_go_out_without_a_role_query()
    {
        var hostEvent = Guid.NewGuid();
        var store = new FakeStore(new Dictionary<Guid, string> { [hostEvent] = NotificationTypes.TripEnquiryReceived });
        var fcm = new FakeFcm();

        var verdicts = await Run(
        [
            Push(1, Owner, UserNotificationRoutes.StayEnquiries, hostEvent),
            Push(2, Owner, UserNotificationRoutes.TaxiRequestRing, kind: NotificationTypes.TaxiRequestRing, expires: Now.AddSeconds(60)),
            Push(3, Owner, UserNotificationRoutes.MyStays, kind: NotificationTypes.StayApproved),
        ], store, fcm);

        Assert.All(verdicts, v => Assert.Equal(PushVerdict.Ack, v));
        Assert.Equal(3, fcm.Sent.Count);
        Assert.Empty(store.SeekerQueries);
    }

    [Theory]
    [InlineData(NotificationTypes.TripStartsIn3Days, null, true)]
    [InlineData(NotificationTypes.TaxiDriverAccepted, null, true)]
    [InlineData(NotificationTypes.TripInvite, null, true)]
    [InlineData(NotificationTypes.TripReminder, UserNotificationRoutes.TripPlan, true)]
    [InlineData(NotificationTypes.TaxiTripEnded, UserNotificationRoutes.TripTaxiStatus, true)]
    [InlineData(NotificationTypes.TripReminder, JourneyNotifications.Route, true)]
    [InlineData(NotificationTypes.TripReminder, UserNotificationRoutes.StayEnquiries, false)]
    [InlineData(NotificationTypes.TaxiTripEnded, UserNotificationRoutes.TaxiBookings, false)]
    [InlineData(NotificationTypes.TripEnquiryReceived, UserNotificationRoutes.ChatConversation, false)]
    [InlineData(NotificationTypes.TaxiRequestRing, UserNotificationRoutes.TaxiRequestRing, false)]
    [InlineData(NotificationTypes.RoleRequestApproved, UserNotificationRoutes.Notifications, false)]
    [InlineData(null, null, false)]
    public void Traveller_push_is_a_traveller_type_or_a_traveller_page(string? type, string? route, bool traveller)
        => Assert.Equal(traveller, TravellerOnly.IsTravellerPush(type, route));

    [Fact]
    public void No_owner_or_driver_route_is_a_traveller_route()
    {
        var ownerRoutes = UserNotificationRoutes.OwnedByKind(ListingKinds.Stay).Concat(UserNotificationRoutes.OwnedByKind(ListingKinds.Taxi))
            .Append(UserNotificationRoutes.ChatConversation).Append(UserNotificationRoutes.Notifications).Append(UserNotificationRoutes.Plans)
            .Append(UserNotificationRoutes.OwnerHome);
        Assert.Empty(TravellerOnly.PushRoutes.Intersect(ownerRoutes));
        Assert.All(UserNotificationRoutes.TravellerTaxiRoutes, r => Assert.Contains(r, TravellerOnly.PushRoutes));
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.DTOs.StayRing;
using RentNearBy.Core.Models.StayRing;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.StayRing;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.StayRing;

// Written for a real PostgreSQL (Category=Integration): skipped without one. The stay ring end to end through the real create
// handler, the ring end pass, the answer route and the delivery store.
[Trait("Category", "Integration")]
public class StayRingIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class NoSignal : IOutboxSignal
    {
        public int Wakes { get; private set; }
        public void Wake() => Wakes++;
    }

    private static CreateTripEnquiryRequest Ask(Guid stay) => new() { Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 2, ListingIds = [stay] };

    private static async Task<(Guid Seeker, Guid Host, Guid Stay, Guid Trip)> SetupAsync(JourneyHarness h, string host = "Host")
    {
        var seeker = await h.AddSeekerAsync("Priya Sharma");
        var owner = await h.AddStayOwnerAsync(host);
        var stay = await h.AddStayAsync(owner, "Pine View Stay");
        var trip = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        return (seeker, owner, stay, trip);
    }

    private async Task<Guid> AskOneAsync(JourneyHarness h, Guid trip, Guid seeker, Guid stay)
    {
        var result = await h.AskAsync(trip, seeker, Ask(stay));
        Assert.Equal(201, StatusOf(result));
        return Assert.Single(DataOf<TripEnquiryBatchDto>(result).Items).Id;
    }

    private async Task<OutboxMessage?> RowAsync(string key)
    {
        await using var read = Db.CreateContext();
        return await read.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(o => o.Key == key);
    }

    // Use cases 1, 2, 14: the ring and the missed ring are written with the enquiry; the "new enquiry" push is still there.
    [IntegrationFact]
    public async Task New_enquiry_rings_the_stay_owner_in_the_same_transaction()
    {
        await using var h = new JourneyHarness(Db);
        var (seeker, host, stay, trip) = await SetupAsync(h);
        var id = await AskOneAsync(h, trip, seeker, stay);

        var ring = await RowAsync(StayRingPushKinds.RingKey(id));
        var missed = await RowAsync(StayRingPushKinds.MissedKey(id));
        Assert.NotNull(ring);
        Assert.NotNull(missed);
        Assert.NotNull(await RowAsync($"enq:{id:N}:new"));
        var push = JsonSerializer.Deserialize<OutboxPushPayload>(ring!.Payload, Json)!;
        Assert.Equal(host, push.UserId);
        Assert.Equal(NotificationTypes.StayEnquiryRing, push.Kind);
        Assert.Equal("Priya", push.Data!["guestName"]);
        Assert.Equal("Pine View Stay", push.Data["stayName"]);
        Assert.DoesNotContain(push.Data.Values, v => v.Contains(seeker.ToString("D")));
        Assert.InRange((missed!.NextAttemptAt - ring.NextAttemptAt).TotalSeconds, 299, 301);

        await using var read = Db.CreateContext();
        var row = await read.TripEnquiries.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Equal(TripEnquiryStatuses.Asked, row.Status);
    }

    // Use case 15: the switch off writes no ring; the enquiry and its normal push are unchanged.
    [IntegrationFact]
    public async Task Switch_off_writes_no_ring_and_the_enquiry_is_as_before()
    {
        await using var h = new JourneyHarness(Db);
        var (seeker, host, stay, trip) = await SetupAsync(h);
        await using (var s = h.NewScope())
        {
            var put = await StayRingHandlers.PutSettings(new UpdateStayRingRequest { RingOn = false }, Principal(host), s.Db, CancellationToken.None);
            Assert.Equal(200, StatusOf(put));
            Assert.False(DataOf<StayRingSettingsDto>(put).RingOn);
            Assert.False(DataOf<StayRingSettingsDto>(put).CanRing);
        }
        var id = await AskOneAsync(h, trip, seeker, stay);
        Assert.Null(await RowAsync(StayRingPushKinds.RingKey(id)));
        Assert.Null(await RowAsync(StayRingPushKinds.MissedKey(id)));
        Assert.NotNull(await RowAsync($"enq:{id:N}:new"));
    }

    [IntegrationFact]
    public async Task Settings_default_on_and_put_needs_a_value()
    {
        await using var h = new JourneyHarness(Db);
        var (_, host, _, _) = await SetupAsync(h);
        await using var s = h.NewScope();
        var get = await StayRingHandlers.GetSettings(Principal(host), s.Db, CancellationToken.None);
        Assert.True(DataOf<StayRingSettingsDto>(get).RingOn);
        Assert.True(DataOf<StayRingSettingsDto>(get).StayLive);
        Assert.Equal(400, StatusOf(await StayRingHandlers.PutSettings(new UpdateStayRingRequest(), Principal(host), s.Db, CancellationToken.None)));
        var stranger = await h.AddStayOwnerAsync("No stay");
        Assert.Equal(404, StatusOf(await StayRingHandlers.GetSettings(Principal(stranger), s.Db, CancellationToken.None)));
    }

    // Use case 7: the owner answers from Enquiries (any device): the pass ends the ring once and cancels the missed ring.
    [IntegrationFact]
    public async Task Answer_from_enquiries_ends_the_ring_once_and_cancels_the_missed_ring()
    {
        await using var h = new JourneyHarness(Db);
        var (seeker, host, stay, trip) = await SetupAsync(h);
        var id = await AskOneAsync(h, trip, seeker, stay);
        var offer = await h.SendOfferAsync(id, host, new SendTripOfferRequest
        {
            CheckIn = h.Start, CheckOut = h.Start.AddDays(2),
            Lines = [new TripOfferLine { RoomName = "Deluxe", Units = 1, Guests = 2, PricePerNight = 2000 }],
        });
        Assert.Equal(201, StatusOf(offer));

        await using (var c = Db.CreateContext()) await StayRingEndPass.RunOnceAsync(c, DateTime.UtcNow, CancellationToken.None);
        await using (var c = Db.CreateContext()) await StayRingEndPass.RunOnceAsync(c, DateTime.UtcNow, CancellationToken.None);

        Assert.NotNull((await RowAsync(StayRingPushKinds.MissedKey(id)))!.PublishedAt);
        var end = await RowAsync(StayRingPushKinds.RingEndKey(id));
        Assert.NotNull(end);
        var push = JsonSerializer.Deserialize<OutboxPushPayload>(end!.Payload, Json)!;
        Assert.Equal(StayRingEndReasons.Answered, push.Data!["reason"]);
        Assert.Equal(host, push.UserId);
    }

    // Use case 8: the enquiry closes (expired or withdrawn): the ring ends with reason Closed.
    [IntegrationFact]
    public async Task Closed_enquiry_ends_the_ring_as_closed()
    {
        await using var h = new JourneyHarness(Db);
        var (seeker, _, stay, trip) = await SetupAsync(h);
        var id = await AskOneAsync(h, trip, seeker, stay);
        await using (var w = Db.CreateContext())
            await w.TripEnquiries.Where(e => e.Id == id).ExecuteUpdateAsync(e => e.SetProperty(x => x.Status, TripEnquiryStatuses.Expired));

        await using (var c = Db.CreateContext()) await StayRingEndPass.RunOnceAsync(c, DateTime.UtcNow, CancellationToken.None);
        var end = JsonSerializer.Deserialize<OutboxPushPayload>((await RowAsync(StayRingPushKinds.RingEndKey(id)))!.Payload, Json)!;
        Assert.Equal(StayRingEndReasons.Closed, end.Data!["reason"]);
    }

    // Use case 3/9: an enquiry still Asked keeps ringing: the pass leaves it, so the missed ring stays due.
    [IntegrationFact]
    public async Task Unanswered_ring_keeps_its_missed_ring_due()
    {
        await using var h = new JourneyHarness(Db);
        var (seeker, _, stay, trip) = await SetupAsync(h);
        var id = await AskOneAsync(h, trip, seeker, stay);
        await using (var c = Db.CreateContext()) await StayRingEndPass.RunOnceAsync(c, DateTime.UtcNow, CancellationToken.None);
        Assert.Null((await RowAsync(StayRingPushKinds.MissedKey(id)))!.PublishedAt);
        Assert.Null(await RowAsync(StayRingPushKinds.RingEndKey(id)));
    }

    // Use cases 5, 6: Accept/Decline cancel the missed ring and stop the other devices; the enquiry is exactly as before.
    [IntegrationTheory]
    [InlineData(StayRingEndReasons.Accepted)]
    [InlineData(StayRingEndReasons.Declined)]
    public async Task Ring_buttons_never_change_the_enquiry(string outcome)
    {
        await using var h = new JourneyHarness(Db);
        var (seeker, host, stay, trip) = await SetupAsync(h);
        var id = await AskOneAsync(h, trip, seeker, stay);
        TripEnquiry before;
        await using (var read = Db.CreateContext()) before = await read.TripEnquiries.AsNoTracking().SingleAsync(e => e.Id == id);

        var signal = new NoSignal();
        await using (var s = h.NewScope())
        {
            var result = await StayRingHandlers.Answer(id, new StayRingAnswerRequest { Outcome = outcome, DeviceToken = "tok-answering" },
                Principal(host), s.Http, s.Db, h.RateLimit, signal, CancellationToken.None);
            Assert.Equal(204, StatusOf(result));
        }

        Assert.NotNull((await RowAsync(StayRingPushKinds.MissedKey(id)))!.PublishedAt);
        await using var after = Db.CreateContext();
        var row = await after.TripEnquiries.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Equal(TripEnquiryStatuses.Asked, row.Status);
        Assert.Equal(before.UpdatedAt, row.UpdatedAt);
        Assert.Equal(before.ExpiresAt, row.ExpiresAt);
        var prefix = StayRingPushKinds.RingEndKey(id) + ":";
        var end = await after.OutboxMessages.AsNoTracking().SingleAsync(o => o.Key.StartsWith(prefix));
        var push = JsonSerializer.Deserialize<OutboxPushPayload>(end.Payload, Json)!;
        Assert.Equal("tok-answering", push.ExceptToken);
        Assert.Equal(outcome, push.Data!["reason"]);
        Assert.Equal(1, signal.Wakes);
    }

    [IntegrationFact]
    public async Task Answer_refuses_a_bad_outcome_another_owner_and_an_unknown_enquiry()
    {
        await using var h = new JourneyHarness(Db);
        var (seeker, host, stay, trip) = await SetupAsync(h);
        var id = await AskOneAsync(h, trip, seeker, stay);
        var other = await h.AddStayOwnerAsync("Other");
        await using var s = h.NewScope();
        var signal = new NoSignal();
        Assert.Equal(400, StatusOf(await StayRingHandlers.Answer(id, new StayRingAnswerRequest { Outcome = "Answered" }, Principal(host), s.Http, s.Db, h.RateLimit, signal, CancellationToken.None)));
        Assert.Equal(403, StatusOf(await StayRingHandlers.Answer(id, new StayRingAnswerRequest { Outcome = "Accepted" }, Principal(other), s.Http, s.Db, h.RateLimit, signal, CancellationToken.None)));
        Assert.Equal(404, StatusOf(await StayRingHandlers.Answer(Guid.NewGuid(), new StayRingAnswerRequest { Outcome = "Accepted" }, Principal(host), s.Http, s.Db, h.RateLimit, signal, CancellationToken.None)));
        Assert.Null((await RowAsync(StayRingPushKinds.MissedKey(id)))!.PublishedAt);
    }

    // Delivery check: a live Asked enquiry rings; answered, switch off or another role does not.
    [IntegrationFact]
    public async Task Delivery_store_knows_which_stay_rings_ended()
    {
        await using var h = new JourneyHarness(Db);
        var (seeker, host, stay, trip) = await SetupAsync(h);
        var live = await AskOneAsync(h, trip, seeker, stay);
        await using var services = new ServiceCollection().AddScoped(_ => Db.CreateContext()).BuildServiceProvider();
        var store = new PushDeliveryStore(services.GetRequiredService<IServiceScopeFactory>());

        Assert.Empty(await store.GetEndedStayRingsAsync([live], CancellationToken.None));
        Assert.Equal(new[] { Guid.Empty }, (await store.GetEndedStayRingsAsync([Guid.Empty], CancellationToken.None)).ToArray());

        await using (var w = Db.CreateContext())
            await w.StayListings.Where(x => x.Id == stay).ExecuteUpdateAsync(x => x.SetProperty(y => y.EnquiryRingOn, false));
        Assert.Equal(new[] { live }, (await store.GetEndedStayRingsAsync([live], CancellationToken.None)).ToArray());
    }

    // Use case 16: role change / suspend end every live ring of the owner (reason Closed) and cancel the missed rings.
    [IntegrationFact]
    public async Task Ending_live_rings_of_an_owner_cancels_the_missed_rings()
    {
        await using var h = new JourneyHarness(Db);
        var (seeker, host, stay, trip) = await SetupAsync(h);
        var id = await AskOneAsync(h, trip, seeker, stay);
        await using (var c = Db.CreateContext())
        {
            await using var tx = await c.Database.BeginTransactionAsync();
            Assert.Equal(1, await StayRingWriter.EndLiveRingsOfOwnerAsync(c, host, DateTime.UtcNow));
            await c.SaveChangesAsync();
            await tx.CommitAsync();
        }
        Assert.NotNull((await RowAsync(StayRingPushKinds.MissedKey(id)))!.PublishedAt);
        var end = JsonSerializer.Deserialize<OutboxPushPayload>((await RowAsync(StayRingPushKinds.RingEndKey(id)))!.Payload, Json)!;
        Assert.Equal(StayRingEndReasons.Closed, end.Data!["reason"]);
    }
}

using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Tests.Integration.Journeys;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.Trips;

public class TripUndoRulesTests
{
    private static readonly DateTime Withdrawn = new(2026, 11, 25, 6, 0, 0, DateTimeKind.Utc);

    private static TripEnquiry Row(string kind = "Stay", string status = "Closed", string? reason = "Withdrawn") => new()
    {
        Id = Guid.NewGuid(), Kind = kind, Status = status, FailReason = reason, ListingName = "x",
        CreatedAt = Withdrawn.AddHours(-3), ExpiresAt = Withdrawn.AddDays(2), UpdatedAt = Withdrawn,
    };

    [Fact]
    public void Windows_are_10_15_20_seconds()
    {
        Assert.Equal(10, TripUndoRules.UndoSeconds);
        Assert.Equal(15, TripUndoRules.ServerWindowSeconds);
        Assert.Equal(20, TripUndoRules.HostPushDelaySeconds);
        Assert.True(TripUndoRules.ServerWindowSeconds < TripUndoRules.HostPushDelaySeconds);
    }

    [Fact]
    public void Restore_is_allowed_up_to_15_seconds_after_the_withdraw()
    {
        Assert.True(TripUndoRules.CanRestore(Row(), Withdrawn.AddSeconds(15)));
        Assert.False(TripUndoRules.CanRestore(Row(), Withdrawn.AddSeconds(15).AddTicks(1)));
    }

    [Theory]
    [InlineData("Taxi", "Closed", "Withdrawn")]
    [InlineData("Stay", "Closed", "BookedElsewhere")]
    [InlineData("Stay", "Expired", null)]
    public void Only_a_withdrawn_stay_row_can_come_back(string kind, string status, string? reason)
        => Assert.False(TripUndoRules.CanRestore(Row(kind, status, reason), Withdrawn.AddSeconds(1)));

    [Fact]
    public void An_ask_whose_window_ended_cannot_come_back()
    {
        var e = Row();
        e.ExpiresAt = Withdrawn.AddSeconds(2);
        Assert.False(TripUndoRules.CanRestore(e, Withdrawn.AddSeconds(3)));
    }

    [Fact]
    public void Withdraw_key_is_unique_per_change_and_fits_the_outbox_key()
    {
        var id = Guid.NewGuid();
        var key = TripUndoRules.WithdrawKey(id, Withdrawn);
        Assert.StartsWith(TripUndoRules.WithdrawKeyPrefix(id), key);
        Assert.StartsWith($"enq:{id:N}:wd:", key);
        Assert.NotEqual(key, TripUndoRules.WithdrawKey(id, Withdrawn.AddMilliseconds(1)));
        Assert.True(key.Length <= 120);
    }

    [Fact]
    public void Restored_line_has_its_text()
        => Assert.Equal("The guest reopened this enquiry.", TripSystemLines.Text(TripSystemLines.Restored));

    [Fact]
    public void Expired_and_declined_offers_do_not_hold_a_host_place()
    {
        TripOffer O(string kind, string status) => new() { Id = Guid.NewGuid(), Kind = kind, Status = status };
        var offers = new[]
        {
            O("Offer", "Expired"), O("Offer", "Declined"), O("Offer", "Superseded"), O("Offer", "Pending"), O("Counter", "Pending"),
        };
        Assert.Equal(2, TripExecutionRules.CountedOffers(offers));
        Assert.True(TripExecutionRules.CountedOffers(offers) < TripExecutionRules.MaxOffersPerEnquiry);
        Assert.Equal(3, TripExecutionRules.CountedOffers([.. offers, O("Offer", "Accepted")]));
    }

    [Fact]
    public void Phone_shared_key_is_left_out_when_not_set()
    {
        var off = System.Text.Json.JsonSerializer.Serialize(new TripEnquiryContext(), TripJson.Options);
        Assert.DoesNotContain("phoneShared", off);
        var on = System.Text.Json.JsonSerializer.Serialize(new TripEnquiryContext { PhoneShared = true }, TripJson.Options);
        Assert.Contains("\"phoneShared\":true", on);
        Assert.True(TripEnquiryContexts.Read(on).PhoneShared);
    }
}

// Withdraw with Undo, Restore and the Quiet step cap on PostgreSQL (RENTNEARBY_TEST_DB); skipped without it.
public class TripUndoJourneyTests(RentNearBy.Tests.Data.TestDatabase db) : RentNearBy.Tests.Integration.IntegrationTestBase(db)
{
    private const long City = 210000001;

    private static string? ErrorTypeOf(Microsoft.AspNetCore.Http.IResult r)
        => (((Microsoft.AspNetCore.Http.IValueHttpResult)r).Value as RentNearBy.Core.DTOs.Responses.ApiResponse<object>)?.Error?.Type;

    private static RentNearBy.Core.DTOs.Requests.CreateTripEnquiryRequest Ask(string step, params Guid[] stays)
        => new() { Kind = TripEnquiryKinds.Stay, Day = 1, Nights = 1, ListingIds = [.. stays], StepKey = step, SharePhone = true };

    private static async Task<Microsoft.AspNetCore.Http.IResult> ShortlistAsync(JourneyHarness h, Guid trip, Guid seeker, Guid stay)
    {
        await using var s = h.NewScope();
        return await RentNearBy.Api.Handlers.TripShortlistHandlers.Add(trip, "stay:1", stay,
            new RentNearBy.Core.DTOs.Requests.AddShortlistRequest { Kind = TripShortlistKinds.Stay, Name = "Stay" },
            new RentNearBy.Api.Validators.AddShortlistRequestValidator(), JourneyHarness.Principal(seeker), s.Uow, s.Db);
    }

    private static async Task<List<Guid>> AskStaysAsync(JourneyHarness h, Guid trip, Guid seeker, int count)
    {
        var stays = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var stay = await h.AddStayAsync(await h.AddStayOwnerAsync($"Host{i}"), $"Stay {i}");
            Assert.Equal(200, JourneyHarness.StatusOf(await ShortlistAsync(h, trip, seeker, stay)));
            stays.Add(stay);
        }
        Assert.Equal(201, JourneyHarness.StatusOf(await h.AskAsync(trip, seeker, Ask("stay:1", [.. stays]))));
        return stays;
    }

    private async Task MakeQuietAsync(Guid trip)
    {
        await using var w = Db.CreateContext();
        var old = DateTime.UtcNow.AddHours(-13);
        await w.TripEnquiries.Where(e => e.TripId == trip && e.Status == TripEnquiryStatuses.Asked)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.CreatedAt, old));
    }

    [IntegrationFact]
    public async Task Three_quiet_asks_free_the_step_and_a_seventh_ask_gets_the_typed_StepFull()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var trip = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", City, 2)));
        await AskStaysAsync(h, trip, seeker, 3);
        await MakeQuietAsync(trip);

        await AskStaysAsync(h, trip, seeker, 3);

        var stay = await h.AddStayAsync(await h.AddStayOwnerAsync("Late"), "Late Stay");
        var refused = await ShortlistAsync(h, trip, seeker, stay);
        Assert.Equal((409, TripEnquiryErrorTypes.StepFull), (JourneyHarness.StatusOf(refused), ErrorTypeOf(refused)));
        var data = (StepFullDto)(((Microsoft.AspNetCore.Http.IValueHttpResult)refused).Value as RentNearBy.Core.DTOs.Responses.ApiResponse<object>)!.Data!;
        Assert.Equal(TripStepSlotRules.CaseTotal, data.Case);
        Assert.Equal((3, 3), (data.QuietAsks.Count, data.WaitingAsks.Count));
    }

    [IntegrationFact]
    public async Task Withdraw_with_undo_holds_the_host_push_and_restore_takes_it_back()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var trip = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", City, 2)));
        var stay = (await AskStaysAsync(h, trip, seeker, 1))[0];
        Guid id;
        await using (var r = Db.CreateContext())
            id = await r.TripEnquiries.Where(e => e.TripId == trip && e.ListingId == stay).Select(e => e.Id).SingleAsync();

        await using (var s = h.NewScope())
            Assert.Equal(200, JourneyHarness.StatusOf(await RentNearBy.Api.Handlers.TripEnquiryHandlers.Withdraw(
                id, JourneyHarness.Principal(seeker), s.Uow, s.Db, h.Chat, h.Notifier, h.Loggers,
                new RentNearBy.Infrastructure.Repositories.OutboxWriter(s.Db), undo: true)));

        await using (var r = Db.CreateContext())
        {
            var held = await r.OutboxMessages.SingleAsync(o => o.Key.StartsWith(TripUndoRules.WithdrawKeyPrefix(id)));
            Assert.True(held.NextAttemptAt > DateTime.UtcNow.AddSeconds(10));
        }

        await using (var s = h.NewScope())
            Assert.Equal(200, JourneyHarness.StatusOf(await RentNearBy.Api.Handlers.TripEnquiryHandlers.Restore(
                id, JourneyHarness.Principal(seeker), s.Uow, s.Db, h.Chat, h.Notifier, h.Loggers)));

        await using var check = Db.CreateContext();
        Assert.False(await check.OutboxMessages.AnyAsync(o => o.Key.StartsWith(TripUndoRules.WithdrawKeyPrefix(id))));
        var row = await check.TripEnquiries.SingleAsync(e => e.Id == id);
        Assert.Equal((TripEnquiryStatuses.Asked, (string?)null), (row.Status, row.FailReason));
        Assert.True(TripEnquiryContexts.Read(row.ContextJson).PhoneShared);

        // A second restore is an idempotent retry.
        await using (var s = h.NewScope())
            Assert.Equal(200, JourneyHarness.StatusOf(await RentNearBy.Api.Handlers.TripEnquiryHandlers.Restore(
                id, JourneyHarness.Principal(seeker), s.Uow, s.Db, h.Chat, h.Notifier, h.Loggers)));
    }

    [IntegrationFact]
    public async Task Withdraw_without_undo_cannot_be_restored()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var trip = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", City, 2)));
        var stay = (await AskStaysAsync(h, trip, seeker, 1))[0];
        Guid id;
        await using (var r = Db.CreateContext())
            id = await r.TripEnquiries.Where(e => e.TripId == trip && e.ListingId == stay).Select(e => e.Id).SingleAsync();
        await using (var s = h.NewScope())
            await RentNearBy.Api.Handlers.TripEnquiryHandlers.Withdraw(id, JourneyHarness.Principal(seeker), s.Uow, s.Db, h.Chat, h.Notifier, h.Loggers);
        await using (var s = h.NewScope())
        {
            var r = await RentNearBy.Api.Handlers.TripEnquiryHandlers.Restore(id, JourneyHarness.Principal(seeker), s.Uow, s.Db, h.Chat, h.Notifier, h.Loggers);
            Assert.Equal((409, TripErrorTypes.UndoWindowOver), (JourneyHarness.StatusOf(r), ErrorTypeOf(r)));
        }
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Hubs;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;
using RentNearBy.Tests.Support;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Trips;

// The owner's UPI ID and WhatsApp number shared in a stay chat: one card, no amount, no status, server-written payload.
public class StayShareCardRulesTests
{
    private static readonly SendMessageRequestValidator Validator = new();

    [Fact]
    public void Validator_accepts_the_share_type_with_an_empty_payload()
        => Assert.True(Validator.Validate(new SendMessageRequest { Type = "stay_contact_share", PayloadJson = "" }).IsValid);

    [Fact]
    public void Validator_refuses_a_share_card_that_answers_a_question()
        => Assert.False(Validator.Validate(new SendMessageRequest
        {
            Type = "stay_contact_share", PayloadJson = "{}", RespondsToMessageId = Guid.NewGuid(),
        }).IsValid);

    [Fact]
    public void Validator_still_refuses_unknown_types_and_empty_payloads_of_other_types()
    {
        Assert.False(Validator.Validate(new SendMessageRequest { Type = "trip_advance_request", PayloadJson = "{}" }).IsValid);
        Assert.False(Validator.Validate(new SendMessageRequest { Type = "quick_reply", PayloadJson = "" }).IsValid);
    }

    [Fact]
    public void Payload_has_only_the_four_keys()
    {
        var p = StayShareRules.Payload(" Lake View ", " host@upi ", "9876543210")!;
        Assert.Equal(new[] { "stayName", "text", "upiId", "whatsApp" }, p.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("Lake View", p["stayName"]);
        Assert.Equal("host@upi", p["upiId"]);
        Assert.Equal("9876543210", p["whatsApp"]);
        Assert.Equal(StayShareRules.FallbackText, p["text"]);
    }

    [Fact]
    public void Payload_leaves_out_missing_fields_and_is_null_when_nothing_is_set()
    {
        var upiOnly = StayShareRules.Payload("Stay", "host@upi", "  ")!;
        Assert.False(upiOnly.ContainsKey("whatsApp"));
        var waOnly = StayShareRules.Payload(null, null, "9876543210")!;
        Assert.False(waOnly.ContainsKey("upiId"));
        Assert.False(waOnly.ContainsKey("stayName"));
        Assert.Null(StayShareRules.Payload("Stay", null, ""));
    }

    [Theory]
    [InlineData("name@bank", true)]
    [InlineData("first.last-1@okaxis", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("name", false)]
    [InlineData("@bank", false)]
    [InlineData("a@b", false)]
    [InlineData("na me@bank", false)]
    [InlineData("name@bank1", false)]
    public void IsValidUpi_vectors(string? upi, bool ok) => Assert.Equal(ok, StayShareRules.IsValidUpi(upi));

    [Fact]
    public void IsValidUpi_length_limits()
    {
        Assert.False(StayShareRules.IsValidUpi(new string('a', 50) + "@" + new string('b', 10)));
        Assert.True(StayShareRules.IsValidUpi(new string('a', 49) + "@" + new string('b', 10)));
    }
}

// ChatHandlers.SendMessage with type stay_contact_share on PostgreSQL (RENTNEARBY_TEST_DB with PostGIS); skipped without it.
public class StayShareCardTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static string? ErrorTypeOf(IResult r) => (((IValueHttpResult)r).Value as ApiResponse<object>)?.Error?.Type;

    private async Task<Guid> ConversationAsync(Guid renter, Guid owner, Guid stay)
    {
        var id = Guid.NewGuid();
        await using var w = Db.CreateContext();
        w.Conversations.Add(new Conversation
        {
            Id = id, RenterId = renter, OwnerId = owner, ListingType = ListingKinds.Stay, ListingId = stay,
            Status = "Active", CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow,
        });
        await w.SaveChangesAsync();
        return id;
    }

    private async Task SetShareAsync(Guid stay, string? upi, string? wa)
    {
        await using var w = Db.CreateContext();
        await w.StayListings.Where(s => s.Id == stay)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpiId, upi).SetProperty(x => x.WhatsAppNumber, wa));
    }

    private static async Task<IResult> SendAsync(JourneyHarness h, Guid conversation, Guid caller, string payload = "{\"upiId\":\"evil@bank\",\"amount\":500}")
    {
        await using var s = h.NewScope();
        return await ChatHandlers.SendMessage(conversation,
            new SendMessageRequest { Type = "stay_contact_share", PayloadJson = payload },
            new SendMessageRequestValidator(), Principal(caller), s.Uow, h.RateLimit, new NullHubContext(),
            new RecordingPublisher(), s.Db, s.Http, h.Cache);
    }

    [IntegrationFact]
    public async Task Owner_with_details_sends_a_server_written_card()
    {
        await using var h = new JourneyHarness(Db);
        var guest = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host, "Lake View Stay");
        await SetShareAsync(stay, "host@upi", "9876543210");
        var conversation = await ConversationAsync(guest, host, stay);

        var result = await SendAsync(h, conversation, host);
        Assert.Equal(201, StatusOf(result));

        await using var r = Db.CreateContext();
        var message = await r.Messages.SingleAsync(m => m.ConversationId == conversation);
        Assert.Equal("stay_contact_share", message.Type);
        using var doc = JsonDocument.Parse(message.PayloadJson);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "stayName", "text", "upiId", "whatsApp" }, keys);
        Assert.Equal("host@upi", doc.RootElement.GetProperty("upiId").GetString());
        Assert.Equal("Lake View Stay", doc.RootElement.GetProperty("stayName").GetString());
        Assert.Equal(StayShareRules.Preview, (await r.Conversations.SingleAsync(c => c.Id == conversation)).LastMessagePreview);
    }

    [IntegrationFact]
    public async Task Guest_caller_is_forbidden()
    {
        await using var h = new JourneyHarness(Db);
        var guest = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        await SetShareAsync(stay, "host@upi", "9876543210");
        var conversation = await ConversationAsync(guest, host, stay);

        Assert.Equal(403, StatusOf(await SendAsync(h, conversation, guest)));
    }

    [IntegrationFact]
    public async Task Nothing_set_is_a_400_ShareDetailsNotSet()
    {
        await using var h = new JourneyHarness(Db);
        var guest = await h.AddSeekerAsync();
        var host = await h.AddStayOwnerAsync();
        var stay = await h.AddStayAsync(host);
        var conversation = await ConversationAsync(guest, host, stay);

        var result = await SendAsync(h, conversation, host);
        Assert.Equal(400, StatusOf(result));
        Assert.Equal("ShareDetailsNotSet", ErrorTypeOf(result));
    }

    private sealed class NullHubContext : IHubContext<ChatHub>
    {
        public IHubClients Clients { get; } = new NullClients();
        public IGroupManager Groups { get; } = new NullGroups();

        private sealed class NullProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private sealed class NullClients : IHubClients
        {
            private static readonly IClientProxy P = new NullProxy();
            public IClientProxy All => P;
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => P;
            public IClientProxy Client(string connectionId) => P;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => P;
            public IClientProxy Group(string groupName) => P;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => P;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => P;
            public IClientProxy User(string userId) => P;
            public IClientProxy Users(IReadOnlyList<string> userIds) => P;
        }

        private sealed class NullGroups : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}

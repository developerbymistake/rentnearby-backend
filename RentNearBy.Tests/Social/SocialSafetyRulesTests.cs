using System.Reflection;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Tests.Buddy.Foundation;

namespace RentNearBy.Tests.Social;

// Spec 25: the community guidelines gate and the chat security rules that need no database.
public class SocialSafetyRulesTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(parts.Prepend(BuddySources.Root()).ToArray()));

    [Fact]
    public void Guidelines_count_as_accepted_only_with_a_time_and_the_current_version()
    {
        var at = DateTime.UtcNow;
        Assert.False(SocialRules.GuidelinesAccepted(null, SocialLimits.GuidelinesVersion));
        Assert.False(SocialRules.GuidelinesAccepted(at, null));
        Assert.False(SocialRules.GuidelinesAccepted(at, 0));
        Assert.False(SocialRules.GuidelinesAccepted(at, SocialLimits.GuidelinesVersion - 1));
        Assert.True(SocialRules.GuidelinesAccepted(at, SocialLimits.GuidelinesVersion));
        Assert.Equal(1, SocialLimits.GuidelinesVersion);
    }

    [Fact]
    public void Guidelines_not_accepted_is_a_403_with_a_message()
    {
        Assert.Equal(403, SocialErrorTypes.StatusOf(SocialErrorTypes.GuidelinesNotAccepted));
        Assert.False(string.IsNullOrWhiteSpace(SocialErrorTypes.MessageOf(SocialErrorTypes.GuidelinesNotAccepted)));
        Assert.Contains(SocialErrorTypes.GuidelinesNotAccepted, SocialErrorTypes.All);
        Assert.False(SocialErrorTypes.IsCap(SocialErrorTypes.GuidelinesNotAccepted));
    }

    [Fact]
    public void Me_dto_carries_guidelines_accepted()
        => Assert.NotNull(typeof(SocialMeDto).GetProperty(nameof(SocialMeDto.GuidelinesAccepted)));

    // The service refuses before anything is charged; the handlers refuse before a rate-limit permit is taken.
    [Fact]
    public void Every_send_path_checks_the_guidelines_first()
    {
        var service = Src("RentNearBy.Infrastructure", "Services", "Social", "SocialChatService.cs");
        var send = service[service.IndexOf("public async Task<BuddyResult<ChatMessageDto>> SendAsync(", StringComparison.Ordinal)..];
        var sendHead = send[..send.IndexOf("SendCoreAsync(me, roomId", StringComparison.Ordinal)];
        Assert.Contains("GuidelinesAcceptedAsync", sendHead);
        var start = service[service.IndexOf("public async Task<BuddyResult<StartedChatDto>> StartAsync(", StringComparison.Ordinal)..];
        var startHead = start[..start.IndexOf("PairAsync(me, him", StringComparison.Ordinal)];
        Assert.Contains("GuidelinesAcceptedAsync", startHead);

        var handlers = Src("RentNearBy.Api", "Handlers", "Social", "SocialChatHandlers.cs");
        foreach (var name in new[] { "public static Task<IResult> TripRoom(", "public static async Task<IResult> Start(", "public static async Task<IResult> Send(" })
        {
            var body = handlers[handlers.IndexOf(name, StringComparison.Ordinal)..];
            var next = body.IndexOf("public static", name.Length, StringComparison.Ordinal);
            Assert.Contains("GuidelinesAcceptedAsync", next < 0 ? body : body[..next]);
        }
    }

    [Fact]
    public void Reads_and_the_room_list_stay_allowed_before_acceptance()
    {
        var handlers = Src("RentNearBy.Api", "Handlers", "Social", "SocialChatHandlers.cs");
        foreach (var name in new[] { "public static Task<IResult> Rooms(", "public static Task<IResult> Messages(", "public static Task<IResult> Room(" })
        {
            var body = handlers[handlers.IndexOf(name, StringComparison.Ordinal)..];
            var next = body.IndexOf("public static", name.Length, StringComparison.Ordinal);
            Assert.DoesNotContain("GuidelinesAcceptedAsync", next < 0 ? body : body[..next]);
        }
    }

    [Fact]
    public void Guidelines_route_is_mapped_seeker_only()
    {
        var endpoints = Src("RentNearBy.Api", "Endpoints", "SocialEndpoints.cs");
        Assert.Contains("MapPost(\"/me/guidelines\", ProfileHandlers.AcceptGuidelines).SeekerOnly()", endpoints);
    }

    // Spec 25 C: chat security facts.
    [Fact]
    public void Chat_requests_carry_text_only_and_no_attachment_field()
    {
        foreach (var type in new[] { typeof(SendChatMessageRequest), typeof(StartChatRequest) })
        {
            var names = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).OrderBy(n => n).ToArray();
            Assert.Equal(type == typeof(SendChatMessageRequest) ? new[] { "ClientMessageId", "Text" } : new[] { "ClientMessageId", "Text", "ToUserId" }, names);
        }
        Assert.DoesNotContain(typeof(ChatMessageDto).GetProperties(), p => p.Name is "Url" or "ImageUrl" or "FileUrl" or "Attachment");
    }

    [Fact]
    public void Chat_text_limit_is_500_and_sends_are_limited_per_sender()
    {
        Assert.Equal(500, SocialLimits.ChatTextMax);
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var limit = SocialRateLimits.ChatSend(me);
        Assert.Equal(30, limit.Max);
        Assert.Contains(me.ToString(), limit.Key);
        Assert.NotEqual(limit.Key, SocialRateLimits.ChatSend(other).Key);
    }

    [Fact]
    public void Rest_and_hub_sends_both_take_the_per_sender_permit()
    {
        Assert.Contains("SocialRateLimits.ChatSend(", Src("RentNearBy.Api", "Handlers", "Social", "SocialChatHandlers.cs"));
        Assert.Contains("SocialRateLimits.ChatSend(", Src("RentNearBy.Api", "Hubs", "ChatHub.cs"));
        Assert.Contains("SocialRateLimits.ChatNew(", Src("RentNearBy.Api", "Handlers", "Social", "SocialChatHandlers.cs"));
    }

    [Fact]
    public void Chat_send_masks_contact_details_before_storing()
    {
        var service = Src("RentNearBy.Infrastructure", "Services", "Social", "SocialChatService.cs");
        Assert.Contains("ContactMask.Apply(clean)", service);
        Assert.Contains("ContactMask.StripInvisible", service);
    }
}

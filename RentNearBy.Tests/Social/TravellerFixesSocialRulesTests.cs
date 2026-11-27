using RentNearBy.Api.Validators.Social;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;

namespace RentNearBy.Tests.Social;

// Owner order 2026-11-28 (USER_APP_ISSUES_2026_11_28.md I-2, I-6, I-9, I-10): the pure numbers and field rules of the social side.
public class TravellerFixesSocialRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 28);

    private static PostFields Fields(int days) =>
        new(SocialRules.WhenKindWithin, 2, null, days, JoinGenders.Anyone, 2, "Easy trek with friends", PostTravelModes.Taxi);

    [Fact]
    public void Profile_limits_are_two_to_three_interests_and_skills_and_one_to_three_places()
        => Assert.Equal((2, 3, 2, 3, 1, 3), (SocialLimits.InterestsMin, SocialLimits.InterestsMax, SocialLimits.SkillsMin, SocialLimits.SkillsMax,
            SocialLimits.PlacesMin, SocialLimits.PlacesMax));

    [Fact]
    public void Interest_catalog_is_the_eight_owner_options()
        => Assert.Equal(new[] { "Mountains", "Treks", "Adventure", "Food", "Culture", "Spiritual", "Road trips", "Camping" }, SocialCatalog.Interests);

    [Fact]
    public void Interests_and_skills_take_two_or_three_from_the_catalog()
    {
        Assert.Null(SocialRules.CatalogList(["Treks"], SocialCatalog.Interests, SocialLimits.InterestsMin, SocialLimits.InterestsMax));
        Assert.NotNull(SocialRules.CatalogList(["Treks", "Food"], SocialCatalog.Interests, SocialLimits.InterestsMin, SocialLimits.InterestsMax));
        Assert.Null(SocialRules.CatalogList(["Treks", "Food", "Culture", "Camping"], SocialCatalog.Interests, SocialLimits.InterestsMin, SocialLimits.InterestsMax));
        Assert.Null(SocialRules.CatalogList(["Treks", "Beaches"], SocialCatalog.Interests, SocialLimits.InterestsMin, SocialLimits.InterestsMax));
        Assert.Null(SocialRules.CatalogList(["Driver"], SocialCatalog.Skills, SocialLimits.SkillsMin, SocialLimits.SkillsMax));
        Assert.NotNull(SocialRules.CatalogList(["Driver", "Cook", "First aid"], SocialCatalog.Skills, SocialLimits.SkillsMin, SocialLimits.SkillsMax));
    }

    [Fact]
    public void Places_visited_are_one_to_three()
    {
        Assert.Null(SocialRules.Places([]));
        Assert.NotNull(SocialRules.Places(["Manali", "Shimla", "Kasol"]));
        Assert.Null(SocialRules.Places(["Manali", "Shimla", "Kasol", "Leh"]));
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(4, null)]
    [InlineData(5, "days")]
    [InlineData(11, "days")]
    public void A_post_lasts_one_to_four_days(int days, string? field)
        => Assert.Equal(field, PublicTripService.FieldError(Fields(days), Today));

    [Fact]
    public void A_new_post_takes_exactly_one_city()
    {
        var validator = new CreatePublicTripRequestValidator();
        CreatePublicTripRequest Req(params long[] keys) => new() { ClientTripId = Guid.NewGuid(), CityKeys = [.. keys], Text = "Hi" };
        Assert.True(validator.Validate(Req(210000001)).IsValid);
        Assert.False(validator.Validate(Req()).IsValid);
        Assert.False(validator.Validate(Req(210000001, 210000002)).IsValid);
        Assert.Equal(4, SocialLimits.PostCitiesMax); // a trip posted from Where to next keeps its cities
    }

    [Fact]
    public void No_weekly_notify_cap_and_nearby_reach_is_fifty()
    {
        Assert.Equal(50, SocialLimits.NearbyPerPostMax);
        Assert.Equal(PostNotifyStates.Queued, PostNotifyRules.NearbyState(true, false, null, Today));
    }

    [Fact]
    public void A_free_member_sends_one_message_per_room()
    {
        Assert.Equal(1, SocialLimits.FreeMessagesPerRoom);
        Assert.False(SocialChatService.SendsFreely(ChatKinds.Direct, isPlus: false, groupChat: true));
        Assert.True(SocialChatService.SendsFreely(ChatKinds.Direct, isPlus: true, groupChat: false));
        Assert.False(SocialChatService.SendsFreely(ChatKinds.Trip, isPlus: true, groupChat: false));
        Assert.True(SocialChatService.SendsFreely(ChatKinds.Trip, isPlus: false, groupChat: true));
        // Direct: Plus, or Plus cannot be bought (payment off) = no lock that leads nowhere.
        Assert.False(SocialChatService.DirectFree(new SeekerLimits(false, true, 1, 1, 1, 3, false, false, false)));
        Assert.True(SocialChatService.DirectFree(new SeekerLimits(true, false, 6, 3, 5, 10, true, true, true)));
        Assert.True(SocialChatService.DirectFree(new SeekerLimits(false, false, 6, 3, 5, 10, true, true, true)));
        // Trip rooms: the group switch, or Plus cannot be bought.
        Assert.False(SocialChatService.TripFree(new SeekerLimits(false, true, 1, 1, 1, 3, false, false, false)));
        Assert.True(SocialChatService.TripFree(new SeekerLimits(false, false, 1, 1, 1, 3, false, false, false)));
        Assert.True(SocialChatService.TripFree(new SeekerLimits(true, true, 6, 3, 5, 10, true, true, true)));
    }

    [Fact]
    public void Payment_off_sends_freely_but_keeps_the_one_message_until_reply_request_rule()
    {
        // Not Plus, Plus cannot be bought (directFree true): no plus lock, but a Request starter waits for the reply.
        Assert.Equal(ChatLocks.AwaitingReply, SocialChatService.LockOf(ChatKinds.Direct, ChatRoomStatuses.Request, true, 1, false,
            isPlus: false, groupChat: true, sentCount: 1, directFree: true));
        Assert.Equal(ChatLocks.None, SocialChatService.LockOf(ChatKinds.Direct, ChatRoomStatuses.Active, true, 3, false,
            isPlus: false, groupChat: true, sentCount: 2, directFree: true));
        // Payment on, not Plus: the plus lock after the one message.
        Assert.Equal(ChatLocks.Plus, SocialChatService.LockOf(ChatKinds.Direct, ChatRoomStatuses.Request, true, 1, false,
            isPlus: false, groupChat: true, sentCount: 1, directFree: false));
        // A real Plus starter is never awaitingReply on an open request.
        Assert.Equal(ChatLocks.None, SocialChatService.LockOf(ChatKinds.Direct, ChatRoomStatuses.Request, true, 1, false,
            isPlus: true, groupChat: true, sentCount: 1, directFree: true));
    }

    [Fact]
    public void Chat_service_passes_the_real_plan_to_the_bump_and_the_free_flags_to_the_locks()
    {
        var src = File.ReadAllText(Path.Combine(RentNearBy.Tests.Buddy.Foundation.BuddySources.Root(),
            "RentNearBy.Infrastructure", "Services", "Social", "SocialChatService.cs"));
        Assert.Contains("Bool(\"plus\", isPlus)", src);
        Assert.Equal(2, src.Split("SendInTxAsync(me, roomId, clientMessageId, clean, myName, limits.IsPlus, DirectFree(limits), TripFree(limits), c)").Length - 1);
        Assert.Equal(3, src.Split("ToRoom(r, me, limits.IsPlus, TripFree(limits), DirectFree(limits))").Length - 1);
        Assert.Equal(1, src.Split("limits.GroupChat").Length - 1);
    }
}

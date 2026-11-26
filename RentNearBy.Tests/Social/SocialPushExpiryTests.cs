using System.Text.Json;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Buddy.Be4;

namespace RentNearBy.Tests.Social;

// Contract 3.6: chat pushes carry an outbox expiry of 1 hour; the other social pushes keep none.
public class SocialPushExpiryTests
{
    private static readonly Guid User = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Other = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Room = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly DateTime Now = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

    private static OutboxPushPayload Parse(string json)
        => JsonSerializer.Deserialize<OutboxPushPayload>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void Direct_and_trip_chat_pushes_expire_one_hour_after_the_write()
    {
        foreach (var spec in new[] { SocialPushCopy.DirectMessage(User, Room, "Aman", Now), SocialPushCopy.TripMessage(User, Room, Now) })
        {
            Assert.Equal(TimeSpan.FromHours(1), spec.Expires);
            Assert.Equal(Now.AddHours(1), Parse(NoticeWriter.Payload(spec, Now)).ExpiresAtUtc);
        }
    }

    [Fact]
    public void Follow_and_join_pushes_have_no_expiry()
    {
        var specs = new[]
        {
            SocialPushCopy.FollowRequest(User, Other, "Aman"),
            SocialPushCopy.NewFollower(User, Other, "Aman", Now),
            SocialPushCopy.FollowAccepted(User, Other, "Aman"),
            SocialPushCopy.JoinRequest(User, Other, Room, "Aman"),
            SocialPushCopy.JoinAccepted(User, Other, Room, "Rahul"),
        };
        foreach (var spec in specs)
        {
            Assert.Null(spec.Expires);
            Assert.Null(Parse(NoticeWriter.Payload(spec, Now)).ExpiresAtUtc);
        }
    }

    [Fact]
    public void A_delayed_push_expires_after_its_delay()
    {
        var spec = SocialPushCopy.DirectMessage(User, Room, "Aman", Now) with { Delay = TimeSpan.FromMinutes(10) };
        Assert.Equal(Now.AddMinutes(70), NoticeWriter.ExpiresAt(spec, Now));
    }
}

using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Buddy.Be1;
using RentNearBy.Infrastructure.Services.Social;

namespace RentNearBy.Tests.Social;

// Mock frames C8c and E13b: data the rows need, without a database.
public class SocialVisualDataTests
{
    [Fact]
    public void Buddy_list_reads_gender_and_counts_only_joined_trips()
    {
        Assert.Contains("gp.\"Gender\"", BuddyListService.BuddiesSql);
        Assert.Contains("AS \"TripsTogether\"", BuddyListService.BuddiesSql);
        Assert.Contains($"\"Status\" = '{TripMemberStatuses.Joined}'", BuddyListService.BuddiesSql);
        Assert.Contains("LIMIT @take)", BuddyListService.BuddiesSql);
    }

    [Fact]
    public void Trip_room_sends_initials_in_photo_order_and_the_city_key()
    {
        var row = new SocialChatService.RoomRow
        {
            RoomId = Guid.NewGuid(), Kind = ChatKinds.Trip, Status = ChatRoomStatuses.Active, TripId = Guid.NewGuid(), TripTitle = "Manali",
            MemberCount = 4, TripCityKey = 210000123, MemberPhotos = ["https://example.com/k.jpg", null, null], MemberNames = ["kavya", "Rahul", null],
        };
        var trip = SocialChatService.ToRoom(row, Guid.NewGuid(), false).Trip!;
        Assert.Equal(new[] { "https://example.com/k.jpg" }, trip.PhotoUrls);
        Assert.Equal(new[] { "K", "R", "?" }, trip.MemberInitials);
        Assert.Equal(210000123L, trip.CityKey);
    }

    [Fact]
    public void Direct_room_has_no_trip_block()
    {
        var row = new SocialChatService.RoomRow { RoomId = Guid.NewGuid(), Kind = ChatKinds.Direct, Status = ChatRoomStatuses.Active };
        Assert.Null(SocialChatService.ToRoom(row, Guid.NewGuid(), false).Trip);
    }
}

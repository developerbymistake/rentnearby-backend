using System.Text.Json;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Tests.Notifications;

public class NotificationDtoArgumentsTests
{
    private const string ChatArgs =
        """{"conversationId":"c1","listingType":"Stay","stayTypeId":null,"day":2,"isOwner":true,"isBlockedByMe":false}""";

    [Fact]
    public void Chat_args_keep_bool_null_and_number_types()
    {
        var args = NotificationDto.ParseArguments(ChatArgs);
        Assert.NotNull(args);
        var root = args.Value;
        Assert.Equal(JsonValueKind.True, root.GetProperty("isOwner").ValueKind);
        Assert.Equal(JsonValueKind.False, root.GetProperty("isBlockedByMe").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("stayTypeId").ValueKind);
        Assert.Equal(2, root.GetProperty("day").GetInt32());
    }

    [Fact]
    public void Dto_serialises_args_as_a_plain_json_map()
    {
        var dto = new NotificationDto { Id = Guid.NewGuid(), Type = "trip_enquiry_received", ActionArguments = NotificationDto.ParseArguments(ChatArgs) };
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var args = doc.RootElement.GetProperty("actionArguments");
        Assert.Equal(JsonValueKind.Object, args.ValueKind);
        Assert.True(args.GetProperty("isOwner").GetBoolean());
        Assert.Equal("c1", args.GetProperty("conversationId").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Missing_or_bad_args_are_null(string? json)
        => Assert.Null(NotificationDto.ParseArguments(json));

    [Fact]
    public void Real_trip_enquiry_push_args_parse()
    {
        var e = new TripEnquiry
        {
            Id = Guid.NewGuid(), TripId = Guid.NewGuid(), Kind = TripEnquiryKinds.Stay, ListingName = "Deodar Homestay",
            CheckIn = new DateOnly(2026, 11, 1), CheckOut = new DateOnly(2026, 11, 2),
        };
        var n = TripNotifications.TripEnquiryReceived(e, new TripPushContext(Guid.NewGuid(), "Rahul", "Deodar Homestay", ChatArgs));
        Assert.Equal(UserNotificationRoutes.ChatConversation, n.ActionRoute);
        Assert.True(NotificationDto.ParseArguments(n.ActionArgumentsJson)!.Value.GetProperty("isOwner").GetBoolean());
    }
}

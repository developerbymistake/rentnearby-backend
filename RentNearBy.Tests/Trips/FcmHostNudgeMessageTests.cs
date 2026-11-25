using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

// The host nudge goes data-only so the app can draw its Reply and Not available buttons in the background too.
public class FcmHostNudgeMessageTests
{
    private static Dictionary<string, string> Data(string route, string args) => new()
    {
        ["notification_type"] = NotificationTypes.ToWireValue(NotificationTypes.TripReminder),
        ["action_route"] = route,
        ["action_args_json"] = args,
    };

    private const string NudgeArgs = "{\"enquiryId\":\"e1\",\"bucket\":\"New\",\"waitingHours\":2,\"actions\":[\"reply\",\"notAvailable\"]}";

    [Fact]
    public void The_host_nudge_is_data_only_with_title_and_body_in_data()
    {
        var message = FcmService.ToMessage(new FcmMessageSpec("tok", "1 traveller waiting", "Waiting 2 h.", Data(UserNotificationRoutes.StayEnquiries, NudgeArgs)));

        Assert.Null(message.Notification);
        Assert.Null(message.Android.Notification);
        Assert.Equal("1 traveller waiting", message.Data["title"]);
        Assert.Equal("Waiting 2 h.", message.Data["body"]);
    }

    [Fact]
    public void Other_reminders_keep_the_visible_shape()
    {
        var message = FcmService.ToMessage(new FcmMessageSpec("tok", "t", "b", Data("/my-enquiries", "{\"enquiryId\":\"e1\"}")));

        Assert.NotNull(message.Notification);
        Assert.False(FcmService.IsHostNudge(Data(UserNotificationRoutes.StayEnquiries, "{\"enquiryId\":\"e1\"}")));
    }
}

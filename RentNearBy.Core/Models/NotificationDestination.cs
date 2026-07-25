namespace RentNearBy.Core.Models;

public sealed record NotificationDestination(string ActionRoute, string? ActionArgumentsJson = null)
{
    public IDictionary<string, string> ToFcmData()
    {
        var data = new Dictionary<string, string> { ["action_route"] = ActionRoute };
        if (ActionArgumentsJson != null) data["action_args_json"] = ActionArgumentsJson;
        return data;
    }
}

// Must match rentnearby_Admin's notification_destinations.dart map keys character-for-character.
public static class AdminNotificationRoutes
{
    public const string InquiryEscalations = "/admin/inquiries/escalations";
    public const string InquiriesList = "/admin/inquiries";
    public const string InquiryDetail = "/admin/inquiry-detail";
    public const string ReportedListings = "/admin/reports";
    public const string GoLiveRequests = "/admin/golive-requests";
}

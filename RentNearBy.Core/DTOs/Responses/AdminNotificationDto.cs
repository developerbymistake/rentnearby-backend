namespace RentNearBy.Core.DTOs.Responses;

// Admin's system-wide feed row — deliberately no IsRead (see AdminNotificationListItem's doc
// comment: read-state has no natural meaning for an admin browsing every Agent's notifications).
public class AdminNotificationDto
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? ActionRoute { get; set; }
    public Dictionary<string, string>? ActionArguments { get; set; }
    // Not part of the NotificationEvent -> AdminNotificationDto Mapster config — set explicitly by
    // the handler from the repository's AdminNotificationListItem.TargetAgentName after Adapt().
    public string? TargetAgentName { get; set; }
    public DateTime CreatedAt { get; set; }
}

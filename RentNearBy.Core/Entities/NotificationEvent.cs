namespace RentNearBy.Core.Entities;

// The notification message itself — ONE row per send, not per recipient. TargetUserId is set for a
// personal/targeted notification (e.g. an agent's new-lead alert); left null is reserved for a
// future broadcast-to-everyone (no producer sets it null yet). Title/Body are precomputed at write
// time so a row stays stable even if the source entity it was about later changes. ActionRoute +
// ActionArgumentsJson together are the redirect target — ActionRoute is a literal AppRoutes.* path
// string, ActionArgumentsJson is exactly the map the target screen expects via Get.arguments (e.g.
// {"id":"<inquiryId>"}) — so the client's tap handler never needs per-Type routing logic.
public class NotificationEvent
{
    public Guid Id { get; set; }
    public Guid? TargetUserId { get; set; }
    public string Type { get; set; } = string.Empty; // RentNearBy.Core.Models.NotificationTypes.*
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? ActionRoute { get; set; }
    public string? ActionArgumentsJson { get; set; }
    public DateTime CreatedAt { get; set; }

    public User? TargetUser { get; set; }
}

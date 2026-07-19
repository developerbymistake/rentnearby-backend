namespace RentNearBy.Core.DTOs.Responses;

public class NotificationDto
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? ActionRoute { get; set; }
    public Dictionary<string, string>? ActionArguments { get; set; }
    // Not part of the NotificationEvent -> NotificationDto Mapster config (read-state lives on a
    // separate join, not a column on the entity) — set explicitly by the handler from the
    // repository's NotificationListItem.IsRead after the Adapt() call.
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}

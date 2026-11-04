using System.Text.Json;

namespace RentNearBy.Core.DTOs.Responses;

public class NotificationDto
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? ActionRoute { get; set; }
    public JsonElement? ActionArguments { get; set; }
    // Not part of the NotificationEvent -> NotificationDto Mapster config (read-state lives on a
    // separate join, not a column on the entity) — set explicitly by the handler from the
    // repository's NotificationListItem.IsRead after the Adapt() call.
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }

    // Keeps the stored JSON types (bool, null, number) as they are; only a JSON object is passed on.
    public static JsonElement? ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

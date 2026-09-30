namespace RentNearBy.Core.DTOs.Responses;

public record BrowsePage<T>(IReadOnlyList<T> Items, bool HasMore, string? NextCursor);

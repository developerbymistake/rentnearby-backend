namespace RentNearBy.Core.DTOs.Requests;

// Partial-patch semantics: null = don't change. Cover photo replace/delete goes through its own
// dedicated endpoint, not this DTO.
public record UpdateServiceRequest(
    string? Name, string? IconName, string? ShortDescription, string? FullDescription,
    int? SortOrder, bool? IsFeatured, bool? IsActive);

namespace RentNearBy.Core.DTOs.Requests;

// Partial-patch semantics: null = don't change.
public record UpdateInclusionRequest(string? Name, string? IconName, int? SortOrder, bool? IsActive);

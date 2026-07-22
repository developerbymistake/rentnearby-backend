namespace RentNearBy.Core.DTOs.Requests;

// Partial-patch semantics: null = don't change.
public record UpdateServiceCategoryRequest(string? Name, string? IconName, string? FormType, int? SortOrder, bool? IsActive, string? AgentRoleLabel = null);

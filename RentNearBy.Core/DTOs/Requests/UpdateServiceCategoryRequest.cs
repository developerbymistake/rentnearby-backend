namespace RentNearBy.Core.DTOs.Requests;

// Partial-patch semantics: null = don't change. ServiceSectionId is intentionally not re-parentable
// here (no precedent for re-parenting in this codebase, e.g. City never moves District) — delete and
// recreate under the correct Section if a category was miscategorized.
public record UpdateServiceCategoryRequest(string? Name, string? IconName, string? FormType, int? SortOrder, bool? IsActive);

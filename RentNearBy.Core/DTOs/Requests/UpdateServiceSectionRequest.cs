namespace RentNearBy.Core.DTOs.Requests;

// Partial-patch semantics: null = don't change (matches UpdateCoinPackRequest/UpdateRoomTypeRequest).
public record UpdateServiceSectionRequest(string? Name, string? IconName, int? SortOrder, bool? IsActive);

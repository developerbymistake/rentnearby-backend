namespace RentNearBy.Core.DTOs.Requests;

// Full-replace, not a diff. An empty list is a legitimate "unassign from all categories" request.
public record SetAgentCategoriesRequest(List<Guid> ServiceCategoryIds);

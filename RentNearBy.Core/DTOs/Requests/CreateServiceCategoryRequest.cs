namespace RentNearBy.Core.DTOs.Requests;

public record CreateServiceCategoryRequest(Guid ServiceSectionId, string Name, string IconName, int SortOrder = 999);

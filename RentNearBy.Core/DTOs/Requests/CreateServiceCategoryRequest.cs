namespace RentNearBy.Core.DTOs.Requests;

// FormType: RentNearBy.Core.Models.ServiceCategoryFormTypes.* — required, not defaulted, so an admin
// consciously picks it rather than silently inheriting "Travel" for a non-travel category.
public record CreateServiceCategoryRequest(Guid ServiceSectionId, string Name, string IconName, string FormType, int SortOrder = 999);

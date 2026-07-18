namespace RentNearBy.Core.DTOs.Requests;

public record CreateServiceSectionRequest(string Name, string IconName, int SortOrder = 999);

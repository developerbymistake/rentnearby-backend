namespace RentNearBy.Core.DTOs.Requests;

public record CreateInclusionRequest(string Name, string IconName, int SortOrder = 999);

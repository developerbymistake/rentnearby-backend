namespace RentNearBy.Core.DTOs.Requests;

public record UpdateAppTabRequest(string? DisplayName, bool? IsActive, string? Reason);

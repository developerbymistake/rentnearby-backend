namespace RentNearBy.Core.DTOs.Requests;

public record UpdateTripExecutionRequest(bool? IsEnabled, string? Reason = null);

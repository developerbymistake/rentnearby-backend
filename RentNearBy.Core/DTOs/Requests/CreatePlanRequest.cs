namespace RentNearBy.Core.DTOs.Requests;

public record CreatePlanRequest(string PlanType, int Price, int Days, int RoomLimit);

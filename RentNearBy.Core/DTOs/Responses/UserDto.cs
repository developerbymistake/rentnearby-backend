namespace RentNearBy.Core.DTOs.Responses;

public class UserDto
{
    public Guid Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool IsActive { get; set; }
    public bool IsPhoneVerified { get; set; }
    public bool HasUsedPhoneChange { get; set; }
    public bool HasUsedFreePlan { get; set; }
    public bool HasUsedFreePlotPlan { get; set; }
    public bool IsContactVisible { get; set; }
    public DateTime CreatedAt { get; set; }
}

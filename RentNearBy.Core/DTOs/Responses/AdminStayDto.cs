namespace RentNearBy.Core.DTOs.Responses;

public class AdminStayDto : StayOwnerDto
{
    public Guid UserId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    // Admin-only.
    public string? RegistrationNumber { get; set; }
    public string? Gstin { get; set; }
}

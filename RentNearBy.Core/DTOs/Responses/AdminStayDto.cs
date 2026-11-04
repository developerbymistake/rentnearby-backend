namespace RentNearBy.Core.DTOs.Responses;

public class AdminStayDto : StayOwnerDto
{
    public Guid UserId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    // Admin-only.
    public string? RegistrationNumber { get; set; }
    public string? Gstin { get; set; }
    // Flags only; the UPI ID and the advance WhatsApp number never reach the admin.
    public bool HasUpiId { get; set; }
    public bool HasAdvanceWhatsApp { get; set; }
}

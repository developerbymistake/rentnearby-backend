namespace RentNearBy.Core.DTOs.Responses;

public class AdminTaxiDto : TaxiOwnerDto
{
    public Guid UserId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    public bool IsDeleted { get; set; }
    // Admin-only. The licence is decrypted on the detail endpoint only; null when absent or unreadable.
    public string? DrivingLicenceNumber { get; set; }
    public string? DateOfBirth { get; set; }   // yyyy-MM-dd
    public int? Age { get; set; }
}

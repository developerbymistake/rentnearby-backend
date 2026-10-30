namespace RentNearBy.Core.DTOs.Responses;

public class DeletedAccountRecordDto
{
    public Guid Id { get; set; }
    public Guid OriginalUserId { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Name { get; set; }
    public DateTime AccountCreatedAt { get; set; }
    public DateTime DeletedAt { get; set; }
    // Admin-only; the licence is decrypted and formatted, null when absent or unreadable.
    public string? DrivingLicenceNumber { get; set; }
    public string? DateOfBirth { get; set; }   // yyyy-MM-dd
    public string? StayRegistrationTypeName { get; set; }
    public string? StayRegistrationOtherName { get; set; }
    public string? StayRegistrationNumber { get; set; }
    public string? StayGstin { get; set; }
}

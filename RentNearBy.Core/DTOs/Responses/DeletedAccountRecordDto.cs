namespace RentNearBy.Core.DTOs.Responses;

public class DeletedAccountRecordDto
{
    public Guid Id { get; set; }
    public Guid OriginalUserId { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string Role { get; set; } = string.Empty;       // SEEKER | STAY_OWNER | TAXI_DRIVER
    public string RoleName { get; set; } = string.Empty;   // Seeker | Stay Owner | Taxi Driver
    public DateTime AccountCreatedAt { get; set; }
    public DateTime DeletedAt { get; set; }
    public bool HasDrivingLicence { get; set; }
    // Admin detail only; always null in the list. Decrypted and formatted, null when absent or unreadable.
    public string? DrivingLicenceNumber { get; set; }
    public string? DateOfBirth { get; set; }   // yyyy-MM-dd
    public string? StayRegistrationTypeName { get; set; }
    public string? StayRegistrationOtherName { get; set; }
    public string? StayRegistrationNumber { get; set; }
    public string? StayGstin { get; set; }
}

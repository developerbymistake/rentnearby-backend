namespace RentNearBy.Core.Entities;

public class DeletedAccountRecord
{
    public Guid Id { get; set; }
    public Guid OriginalUserId { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Name { get; set; }
    public DateTime AccountCreatedAt { get; set; }
    public DateTime DeletedAt { get; set; }

    // Latest taxi/stay identity values, copied before the listing rows are purged; gone with the row after 180 days.
    public string? DrivingLicenceNumberEnc { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? StayRegistrationTypeName { get; set; }
    public string? StayRegistrationOtherName { get; set; }
    public string? StayRegistrationNumber { get; set; }
    public string? StayGstin { get; set; }
}

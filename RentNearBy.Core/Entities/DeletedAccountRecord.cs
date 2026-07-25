namespace RentNearBy.Core.Entities;

public class DeletedAccountRecord
{
    public Guid Id { get; set; }
    public Guid OriginalUserId { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Name { get; set; }
    public DateTime AccountCreatedAt { get; set; }
    public DateTime DeletedAt { get; set; }
}

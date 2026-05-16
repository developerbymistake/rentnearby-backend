namespace RentNearBy.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? GmailId { get; set; }
    public bool IsAdmin { get; set; } = false;
    public bool OtpVerified { get; set; } = false;
    public bool HasUsedFreePlan { get; set; } = false;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
}

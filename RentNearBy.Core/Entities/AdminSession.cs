namespace RentNearBy.Core.Entities;

public class AdminSession
{
    public Guid Id { get; set; }
    public Guid AdminId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LoggedOutAt { get; set; }

    public Admin Admin { get; set; } = null!;
}

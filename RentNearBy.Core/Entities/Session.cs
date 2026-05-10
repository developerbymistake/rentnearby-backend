namespace RentNearBy.Core.Entities;

public class Session
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    public User User { get; set; } = null!;
}

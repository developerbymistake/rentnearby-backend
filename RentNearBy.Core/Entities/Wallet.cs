namespace RentNearBy.Core.Entities;

public class Wallet
{
    public Guid UserId { get; set; }
    public int Balance { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}

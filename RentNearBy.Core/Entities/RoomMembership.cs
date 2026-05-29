namespace RentNearBy.Core.Entities;

public class RoomMembership
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string PlanType { get; set; } = string.Empty; // FREE or PAID
    public DateTime ValidFrom { get; set; }
    public DateTime ValidUntil { get; set; }
    public int MaxRooms { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}

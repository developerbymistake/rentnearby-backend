namespace RentNearBy.Core.Entities;

public class UserBlock
{
    public Guid Id { get; set; }
    public Guid BlockerId { get; set; }
    public Guid BlockedId { get; set; }
    public DateTime CreatedAt { get; set; }
}

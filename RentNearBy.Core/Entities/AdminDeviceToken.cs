namespace RentNearBy.Core.Entities;

public class AdminDeviceToken
{
    public Guid Id { get; set; }
    public Guid AdminId { get; set; }
    public string Token { get; set; } = string.Empty;
    public bool IsValid { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Admin Admin { get; set; } = null!;
}

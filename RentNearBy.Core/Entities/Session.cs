namespace RentNearBy.Core.Entities;

public class Session
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LoggedOutAt { get; set; }
    // D-34 / FB-42: why the server ended the session (SessionRevokeReasons); null = logout or expiry.
    public string? RevokeReason { get; set; }

    public User User { get; set; } = null!;
}

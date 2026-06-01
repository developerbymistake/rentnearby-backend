namespace RentNearBy.Core.Entities;

public class NotificationLog
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime SentAt { get; set; }

    public User User { get; set; } = null!;
}

namespace RentNearBy.Infrastructure.Services;

public class MembershipExpiredMessage
{
    public Guid UserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public DateTime ExpiredAt { get; set; }
}

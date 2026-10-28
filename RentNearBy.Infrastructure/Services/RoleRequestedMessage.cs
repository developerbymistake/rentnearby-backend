namespace RentNearBy.Infrastructure.Services;

public class RoleRequestedMessage
{
    public Guid RequestId { get; set; }
    public string ToRoleName { get; set; } = string.Empty;
}

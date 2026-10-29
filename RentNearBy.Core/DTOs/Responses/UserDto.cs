namespace RentNearBy.Core.DTOs.Responses;

public class UserDto
{
    public Guid Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool IsActive { get; set; }
    public bool IsPhoneVerified { get; set; }
    public bool HasUsedPhoneChange { get; set; }
    public bool IsContactVisible { get; set; }
    public string Role { get; set; } = string.Empty;
    public bool RoleConfirmed { get; set; }
    public bool RoleTabActive { get; set; } = true;   // false while the role's tab is off; the role itself is kept
    public string? RoleRequestStatus { get; set; }
    public string? RoleRequestReason { get; set; }
    public DateTime? NextRoleRequestAt { get; set; }
    public string? StayStatus { get; set; }   // Stay Owner only; null when no live stay row
    public string? TaxiStatus { get; set; }   // Taxi Driver only; null when no live taxi row
    public bool RoleCorrectionUsed { get; set; }
    public bool CanCorrectRole { get; set; }   // one-time self-service change while the stay/taxi was never submitted
    public bool RoleLocked { get; set; }   // stay/taxi under review, approved, or ever approved
    public DateTime CreatedAt { get; set; }
}

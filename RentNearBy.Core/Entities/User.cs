using RentNearBy.Core.Models;

namespace RentNearBy.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsPhoneVerified { get; set; } = false;
    public bool HasUsedPhoneChange { get; set; } = false;
    public bool IsContactVisible { get; set; } = false;
    public Guid RoleId { get; set; } = UserRoles.SeekerId;
    public DateTime? RoleConfirmedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Role Role { get; set; } = null!;
    public Wallet? Wallet { get; set; }
}

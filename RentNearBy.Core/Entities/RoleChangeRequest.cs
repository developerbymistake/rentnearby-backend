using RentNearBy.Core.Models;

namespace RentNearBy.Core.Entities;

public class RoleChangeRequest
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid FromRoleId { get; set; }
    public Guid ToRoleId { get; set; }
    public string Status { get; set; } = RoleChangeRequestStatuses.Pending;
    public DateTime RequestedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public Guid? ReviewedByAdminId { get; set; }
    public string? RejectionReason { get; set; }

    public User User { get; set; } = null!;
    public Role FromRole { get; set; } = null!;
    public Role ToRole { get; set; } = null!;
    public Admin? ReviewedByAdmin { get; set; }
}

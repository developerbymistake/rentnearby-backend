using RentNearBy.Core.Models;

namespace RentNearBy.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Username { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsPhoneVerified { get; set; } = false;
    public bool HasUsedPhoneChange { get; set; } = false;
    public bool RoleCorrectionUsed { get; set; } = false;
    public bool IsContactVisible { get; set; } = false;
    public Guid RoleId { get; set; } = UserRoles.SeekerId;
    public DateTime? RoleConfirmedAt { get; set; }
    // HMAC(PlayBilling:AccountIdSalt, Id) sent to Play as obfuscatedAccountId; written once, never changed.
    public string? PlayAccountKey { get; set; }
    public DateTime? BuddyRequestsHeldUntil { get; set; }
    // Written only by the raw SQL of UserPhotoHandlers and AdminUserPhotoHandlers; url null + updatedAt set = removed by the admin.
    public string? ProfilePhotoUrl { get; set; }
    public string? ProfilePhotoPath { get; set; }
    public DateTime? ProfilePhotoUpdatedAt { get; set; }
    // Lifetime counters (admin only). Written only by TripCompletionCounter with an atomic +1; never decreases.
    public int CompletedTripsCount { get; set; }
    public int CompletedBookingsCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Role Role { get; set; } = null!;
}

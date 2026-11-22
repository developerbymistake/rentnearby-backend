using System.Text.Json.Serialization;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.DTOs.Responses;

public class AdminUserDto
{
    public Guid Id { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public bool IsPhoneVerified { get; set; }
    public string? Name { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public int TotalStays { get; set; }
    public int LiveStayListings { get; set; }   // stays under review: the only stay state that blocks a role change
    public string? StayStatus { get; set; }
    public int TotalTaxis { get; set; }
    public int LiveTaxiListings { get; set; }   // taxis under review
    public string? TaxiStatus { get; set; }
    public AdminUserPlanSummaryDto? Plan { get; set; }   // active paid/trial/granted plan; null = default plan
    public string Role { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;

    public string? Username { get; set; }
    public DateTime? BuddyRequestsHeldUntil { get; set; }
    public string? ProfilePhotoUrl { get; set; }   // list: 160 face thumb; detail: the stored 640 picture

    // Lifetime counters, admin user detail only (D-25): the one that matches the current role, the other key absent.
    // Never set by the users list.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CompletedTrips { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CompletedBookings { get; set; }

    public void SetCompletedCounters(string roleKey, int completedTrips, int completedBookings)
    {
        CompletedTrips = roleKey == UserRoles.Seeker ? completedTrips : null;
        CompletedBookings = roleKey is UserRoles.StayOwner or UserRoles.TaxiDriver ? completedBookings : null;
    }
}

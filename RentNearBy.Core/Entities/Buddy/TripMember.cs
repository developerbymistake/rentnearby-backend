using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Core.Entities.Buddy;

// One row per trip and person; the owner has no row.
public class TripMember
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public Guid UserId { get; set; }
    public string Status { get; set; } = TripMemberStatuses.Invited;
    public Guid InvitedByUserId { get; set; }
    public DateTime InvitedAt { get; set; }
    public DateTime InviteExpiresAt { get; set; }
    public DateTime? JoinedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public DateTime? ExpensePromptDoneAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Version { get; set; } = 1;
    // Invite | Request (joined through a public post join request).
    public string JoinedVia { get; set; } = "Invite";
}

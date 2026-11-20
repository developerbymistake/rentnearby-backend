namespace RentNearBy.Core.Interfaces.Buddy;

// B1 leaves a short-lived mark per (finder, person); B2 creates a new request only with that mark.
public interface IBuddyFoundMarks
{
    Task MarkAsync(Guid finderId, Guid personId);

    Task<bool> HasAsync(Guid finderId, Guid personId);
}

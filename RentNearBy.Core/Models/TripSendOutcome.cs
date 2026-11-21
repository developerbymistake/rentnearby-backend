namespace RentNearBy.Core.Models;

// Result of a once-only trip push. Duplicate = the dedupe key was already logged (an earlier pass sent it);
// Failed = nothing committed (DB error), so the key is still free and the same send must be retried.
public enum TripSendOutcome
{
    Sent,
    Duplicate,
    Failed,
}

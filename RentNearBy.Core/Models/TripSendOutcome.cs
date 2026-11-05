namespace RentNearBy.Core.Models;

// Result of a once-only trip push. Duplicate = the dedupe key was already logged (an earlier pass sent it);
// Failed = nothing committed (DB error), so the key is still free and the same send must be retried.
public enum TripSendOutcome
{
    Sent,
    Duplicate,
    Failed,
}

public static class TripSendOutcomes
{
    // A schedule cursor (host reminders) moves past a slot only when that slot's key is logged.
    public static bool AdvancesCursor(TripSendOutcome outcome) => outcome != TripSendOutcome.Failed;
}

// Admin "Reminders N" = host reminders really sent for the current proof: logged keys proof:{id}:{proofSentAt}:{k}, k >= 1
// (k = 0 is the proof push itself). HostReminderCount stays the internal cursor (it skips missed slots).
public static class HostReminderTally
{
    public static string KeyPrefix(Guid enquiryId, DateTime proofSentAt)
    {
        var key = TripAdvanceRules.ProofKey(enquiryId, proofSentAt, 0);
        return key[..(key.LastIndexOf(':') + 1)];
    }

    public static int Sent(IEnumerable<string> loggedKeys, Guid enquiryId, DateTime? proofSentAt)
    {
        if (proofSentAt is not { } at) return 0;
        var prefix = KeyPrefix(enquiryId, at);
        return loggedKeys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => int.TryParse(k.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var slot) ? slot : 0)
            .Where(slot => slot >= 1)
            .Distinct()
            .Count();
    }
}

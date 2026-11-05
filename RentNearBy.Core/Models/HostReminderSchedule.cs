using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// Host "proof sent" reminders on an absolute schedule: nominal slot k = ProofSentAt + k x 60 min (k >= 1).
// A slot in quiet hours (22:00-07:00 IST) moves to 07:00 IST; a slot less than one interval after the previous
// kept slot is dropped (no burst after the night); only slots before HoldUntil; at most MaxReminders (the first
// MaxReminders - 1 and the last).
// HostReminderCount = n means reminders 1..n-1 are done and slot n (1-based) is next; the proof push itself is 0.
public static class HostReminderSchedule
{
    public const int IntervalMinutes = 60, QuietFromHourIst = 22, QuietToHourIst = 7, MaxReminders = 12;

    public static IReadOnlyList<DateTime> Slots(DateTime proofSentAtUtc, DateTime holdUntilUtc)
    {
        var slots = new List<DateTime>();
        for (var k = 1; ; k++)
        {
            var nominal = proofSentAtUtc.AddMinutes((double)k * IntervalMinutes);
            if (nominal >= holdUntilUtc) break;
            var at = IstTime.WakeUtc(nominal, QuietFromHourIst, QuietToHourIst);
            if (at >= holdUntilUtc) continue;
            if (slots.Count > 0 && at < slots[^1].AddMinutes(IntervalMinutes)) continue;
            slots.Add(at);
        }
        // Over the cap the last waking slot before the hold ends is kept: that reminder matters most.
        if (slots.Count > MaxReminders) slots.RemoveRange(MaxReminders - 1, slots.Count - MaxReminders);
        return slots;
    }

    // The next slot still to send; null when every slot is done or the schedule is empty.
    public static DateTime? NextDue(DateTime proofSentAtUtc, DateTime holdUntilUtc, int reminderCount)
    {
        var slots = Slots(proofSentAtUtc, holdUntilUtc);
        var index = Math.Max(reminderCount, 1) - 1;
        return index < slots.Count ? slots[index] : null;
    }

    // The latest slot due by now (missed ones are skipped, never sent in a burst): its 1-based number and the count to
    // store after sending it. Null when nothing is due.
    public static (int Number, int NextCount)? Due(DateTime proofSentAtUtc, DateTime holdUntilUtc, int reminderCount, DateTime nowUtc)
    {
        if (nowUtc >= holdUntilUtc) return null;
        var slots = Slots(proofSentAtUtc, holdUntilUtc);
        var first = Math.Max(reminderCount, 1) - 1;
        if (first >= slots.Count || slots[first] > nowUtc) return null;
        var last = first;
        while (last + 1 < slots.Count && slots[last + 1] <= nowUtc) last++;
        return (last + 1, last + 2);
    }

    // What the app shows as the next reminder: only while the proof is sent and the hold runs.
    public static DateTime? Next(TripEnquiry e, DateTime nowUtc)
    {
        if (e.AdvanceStatus != TripAdvanceStatuses.ProofSent || e.Status != TripEnquiryStatuses.ConfirmedByBoth) return null;
        if (e.HoldUntil is not { } hold || hold <= nowUtc || e.ProofSentAt is not { } proof) return null;
        return NextDue(proof, hold, e.HostReminderCount);
    }

    // One identity per enquiry for the device tray (Android tag / FCM collapse key) and the inbox row.
    public static string CollapseKey(Guid enquiryId) => $"advance-proof:{enquiryId:N}";
}

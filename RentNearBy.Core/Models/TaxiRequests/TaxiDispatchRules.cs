namespace RentNearBy.Core.Models.TaxiRequests;

// Section 12 of the contract and the numbers of MASTER_PLAN 2.6 and 2.7, in one place.
public static class TaxiDispatchRules
{
    public const int RingSeconds = 300;
    public const int ConfirmSeconds = 900;
    public const int AcceptBatch = 3;
    public const int MaxAccepts = 9;
    public const int MaxLiveRings = 3;
    public const int QueueWaitSeconds = 600;
    public const int RingMissLimit = 3;
    public const int TestRingsPerHour = 6;
    public const int TestRingSeconds = 30;
    public const int CreatesPerHour = 10;
    public const int TripNameMinLength = 1;
    public const int TripNameMaxLength = 60;
    public const int MaxDayKm = 2000;
    public const int MaxDayMinutes = 1440;
    public const int MaxLatestRatings = 10;
    public const int OfferListPageSize = 20;
    // Driver home "Missed" tile (Bug 25): missed rings of the last 7 days, the admin answer window too.
    public const int MissedWindowDays = 7;
    public const int AckPerMinutePerIp = 30;
    public const int AckPerMinutePerOffer = 5;
    public const int ReadinessReportsPerHour = 20;
    // O16: per user, per minute.
    public const int ReadsPerMinute = 60;
    public const int ActionsPerMinute = 30;
    public const int AckTokenBytes = 16;
    public const int RingTtlMinSeconds = 1;
    public const int RingTtlMaxSeconds = RingSeconds;
    public const int AutoCompleteMinutesAfterMidnight = 5;
    public const long FairQueueStride = 1_000_000;
    public const int DefaultAcceptTarget = AcceptBatch;
    public const int ChooseReminderFirstSeconds = 2 * 60 * 60;
    public const int ChooseReminderSecondSeconds = 24 * 60 * 60;

    // O7: armed when the first accept arrives (or a lapsed selection leaves accepts); a reminder due at or after the
    // stale expiry is never created.
    public static IReadOnlyList<(string Kind, DateTime DueAtUtc)> ChooseReminderTimes(DateTime nowUtc, DateOnly dateFrom)
    {
        var stale = StaleDueAt(dateFrom);
        return new (string Kind, DateTime DueAtUtc)[]
            {
                (DueActionKinds.ChooseReminderFirst, nowUtc.AddSeconds(ChooseReminderFirstSeconds)),
                (DueActionKinds.ChooseReminderSecond, nowUtc.AddSeconds(ChooseReminderSecondSeconds)),
            }
            .Where(t => t.DueAtUtc < stale)
            .ToList();
    }

    // A reminder goes out only while the traveller still has to choose: Collecting, a live accepted driver, before the stale expiry.
    public static bool ChooseReminderDue(string state, int liveAccepted, DateTime nowUtc, DateOnly dateFrom)
        => state == RequestStates.Collecting && liveAccepted > 0 && nowUtc < StaleDueAt(dateFrom);

    // O12, the candidate rule of TaxiCandidateQuery: a route to MatchToCityKey, and from MatchFromCityKey when set. A null
    // key never matches (as in the matcher), except an end still pending in this save: its point moved and the city
    // lookup did not answer in time, so it may yet be the city of the offer.
    public static bool RoutesStillServe(long matchToCityKey, long? matchFromCityKey, IEnumerable<RouteServeKeys> routes)
        => routes.Any(r => EndServes(r.ToCityKey, r.ToPending, matchToCityKey)
                           && (matchFromCityKey is not { } from || EndServes(r.FromCityKey, r.FromPending, from)));

    private static bool EndServes(long? key, bool pending, long match) => key == match || (key is null && pending);

    // Ask more (P4): 3 more accepts, never more than 9 in all; a target is never lowered.
    public static int AskMoreTarget(int acceptedCount) => Math.Min(MaxAccepts, acceptedCount + AcceptBatch);

    public static int AskMoreTarget(int acceptedCount, int acceptTarget) => Math.Max(acceptTarget, AskMoreTarget(acceptedCount));

    // O5: an exhausted request rings nobody on its own; while it still lacks accepts, ask more is how the traveller rings
    // new drivers (also when 3 more would pass the target or the cap of 9 is set).
    public static bool CanAskMore(string state, int acceptedCount, int acceptTarget, bool queueExhausted = false)
        => RequestStates.Dispatching.Contains(state)
           && ((acceptedCount + AcceptBatch > acceptTarget && acceptTarget < MaxAccepts) || (queueExhausted && acceptedCount < acceptTarget));

    public static bool StillAsking(string state, bool queueExhausted, bool ringLive, int acceptedCount, int acceptTarget)
        => RequestStates.Dispatching.Contains(state) && !queueExhausted && (ringLive || acceptedCount < acceptTarget);

    public static int RingsNeeded(int acceptTarget, int acceptedCount, int ringing)
        => Math.Clamp(Math.Min(MaxLiveRings, acceptTarget - acceptedCount - ringing), 0, MaxLiveRings);

    // The request ends as NotBooked at 00:00 IST after its first needed day (P6): that day has passed.
    public static DateTime StaleDueAt(DateOnly dateFrom) => IstTime.IstMidnightUtc(dateFrom.AddDays(1));

    // Booked becomes InProgress at 00:00 IST of the first needed day.
    public static DateTime BookingStartAt(DateOnly dateFrom) => IstTime.IstMidnightUtc(dateFrom);

    // 00:05 IST on the day after the last needed day.
    public static DateTime AutoCompleteAt(DateOnly dateTo)
        => IstTime.IstMidnightUtc(dateTo.AddDays(1)).AddMinutes(AutoCompleteMinutesAfterMidnight);

    public static bool IsTripNameLengthValid(int length) => length is >= TripNameMinLength and <= TripNameMaxLength;
}

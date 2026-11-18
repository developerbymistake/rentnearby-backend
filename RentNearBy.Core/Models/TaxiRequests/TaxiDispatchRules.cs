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
    public const int AckPerMinutePerIp = 30;
    public const int AckPerMinutePerOffer = 5;
    public const int ReadinessReportsPerHour = 20;
    public const int AckTokenBytes = 16;
    public const int RingTtlMinSeconds = 1;
    public const int RingTtlMaxSeconds = RingSeconds;
    public const int AutoCompleteMinutesAfterMidnight = 5;
    public const long FairQueueStride = 1_000_000;
    public const int DefaultAcceptTarget = AcceptBatch;

    // Ask more (P4): 3 more accepts, never more than 9 in all.
    public static int AskMoreTarget(int acceptedCount) => Math.Min(MaxAccepts, acceptedCount + AcceptBatch);

    public static bool CanAskMore(string state, int acceptedCount, int acceptTarget)
        => RequestStates.Dispatching.Contains(state) && acceptedCount + AcceptBatch > acceptTarget && acceptTarget < MaxAccepts;

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

namespace RentNearBy.Core.Models;

// Whether a segment journey's own "tomorrow" push (jrn:e) is left to the trip's travel-eve push (trv:{tripId}:{date}).
// Covered when the trip push is already logged, or when the trip timer will really send it: trip execution is on, the
// trip qualifies (TripEveCovers) and its grace after the travel-eve start has not passed. After the grace with no trv key
// logged (timer off, a failed page, a long outage) the journey sends its own push, so the traveller is never left without one.
public static class JourneyEveCover
{
    public const int GraceHours = 2;

    public static DateTime FallbackUtc(DateOnly travelDate)
        => IstTime.EveDueUtc(travelDate, TripReminderRules.TravelEveHourIst).AddHours(GraceHours);

    public static bool Covered(bool tripPushLogged, bool tripExecutionEnabled, bool tripEveCovers, DateOnly travelDate, DateTime nowUtc)
    {
        if (tripPushLogged) return true;
        if (!tripExecutionEnabled || !tripEveCovers) return false;
        return nowUtc < FallbackUtc(travelDate);
    }
}

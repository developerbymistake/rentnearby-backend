using System.Globalization;

namespace RentNearBy.Core.Models;

// "Your journey is tomorrow": the same eve rule as the trip travel-eve (18:00 IST the day before, until midnight).
// The key carries the date, so a journey moved to a new date is reminded again.
public static class JourneyEveRules
{
    public const string KeyPrefix = "jrn:e:";

    public static bool InWindow(DateOnly travelDate, DateTime nowUtc) => IstTime.InEveWindow(travelDate, nowUtc);

    public static string Key(Guid journeyId, DateOnly travelDate)
        => $"{KeyPrefix}{journeyId:N}:{travelDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
}

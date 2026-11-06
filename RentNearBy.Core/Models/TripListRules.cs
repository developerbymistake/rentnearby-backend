using System.Linq.Expressions;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// My Trips: a trip is past once completed or the day after its check-out morning has begun (IST); the check-out day is still active.
public static class TripListRules
{
    public const string Active = "active", Past = "past", Upcoming = "upcoming", Completed = "completed";

    // upcoming and completed are the old names of active and past; null = not a list state.
    public static string? Resolve(string? state)
    {
        var text = string.IsNullOrWhiteSpace(state) ? Active : state.Trim().ToLowerInvariant();
        return text switch
        {
            Active or Upcoming => Active,
            Past or Completed => Past,
            _ => null,
        };
    }

    public static bool IsPast(string status, DateOnly? endDate, DateOnly todayIst)
        => status == TripStatuses.Completed || (endDate is { } end && end.AddDays(1) < todayIst);

    // Same rule as IsPast, as a query predicate (EndDate + 1 < today  <=>  EndDate < today - 1).
    public static Expression<Func<Trip, bool>> PastPredicate(DateOnly todayIst)
    {
        var cutoff = todayIst.AddDays(-1);
        return t => t.Status == TripStatuses.Completed || (t.EndDate != null && t.EndDate < cutoff);
    }

    public static Expression<Func<Trip, bool>> ActivePredicate(DateOnly todayIst)
    {
        var cutoff = todayIst.AddDays(-1);
        return t => t.Status != TripStatuses.Completed && (t.EndDate == null || t.EndDate >= cutoff);
    }
}

using System.Text.Json;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Utils;

namespace RentNearBy.Core.Models;

public sealed record TripWrongCity(int Wrong, int Total, int? FirstWrongDay, string? DayCity)
{
    public bool Any => Wrong > 0;
    public static readonly TripWrongCity None = new(0, 0, null, null);
}

// Per-leg date rules of a multi-city trip. Dates are the truth: a row's day numbers drift when the start date moves.
public static class TripLegRules
{
    // Stay: CheckIn .. CheckOut - 1. Taxi: CheckIn + (d - min(days)) per stored day, else CheckIn .. CheckOut - 1.
    public static List<DateOnly> DatesOf(TripEnquiry e)
    {
        if (e.CheckIn is not { } first) return [];
        if (e.Kind == TripEnquiryKinds.Taxi && ReadDays(e.DaysJson) is { Length: > 0 } days)
        {
            var min = days.Min();
            return days.Distinct().Order().Select(d => first.AddDays(d - min)).ToList();
        }
        var end = e.CheckOut is { } last && last > first ? last : first.AddDays(1);
        var dates = new List<DateOnly>();
        for (var d = first; d < end; d = d.AddDays(1)) dates.Add(d);
        return dates;
    }

    public static int? DayOf(DateOnly start, DateOnly date, int dayCount)
    {
        var day = date.DayNumber - start.DayNumber + 1;
        return day >= 1 && day <= dayCount ? day : null;
    }

    public static int LegEndNight(TripPlan plan, int day)
    {
        if (plan.Days is not { Count: > 0 }) return day;
        return Math.Max(day, plan.LegEnd(plan.LegIndexOf(day)));
    }

    // The night after a leg's last day is in that leg's city; check-out is the next leg's travel-day morning.
    public static DateOnly StayCheckOut(TripPlan plan, DateOnly start, int day, bool allNights, int? nights)
    {
        var checkIn = TripExecutionRules.NightDate(start, day);
        var left = Math.Max(1, LegEndNight(plan, day) - day + 1);
        if (nights is { } n) return checkIn.AddDays(Math.Clamp(n, 1, left));
        return checkIn.AddDays(allNights ? left : 1);
    }

    public static bool DaysInOneLeg(TripPlan plan, IReadOnlyList<int> days)
        => days.Count <= 1 || days.Select(plan.LegIndexOf).Distinct().Count() == 1;

    public static bool IsNoStayNight(TripPlan plan, int day)
        => plan.Days?.FirstOrDefault(d => d != null && d.Day == day)?.Stay?.Skipped == true;

    public static int? FirstNoStayNight(TripPlan plan, DateOnly start, DateOnly checkIn, DateOnly checkOut)
    {
        var end = checkOut > checkIn ? checkOut : checkIn.AddDays(1);
        for (var date = checkIn; date < end; date = date.AddDays(1))
        {
            var day = date.DayNumber - start.DayNumber + 1;
            if (IsNoStayNight(plan, day)) return day;
        }
        return null;
    }

    // The leg the owner was asked for: Context.City by folded name, else the nearest leg to the listing, else unknown.
    public static int? HomeLegIndex(TripEnquiry e, TripPlan plan)
    {
        var legs = LegsOf(plan);
        if (legs.Count == 0) return null;

        var city = SearchKeys.Fold(TripEnquiryContexts.Read(e.ContextJson).City);
        if (city.Length > 0)
        {
            var match = legs.FirstOrDefault(l => SearchKeys.Fold(l.Name) == city);
            if (match.Index > 0) return match.Index;
        }

        if (e.ListingLatitude is not { } lat || e.ListingLongitude is not { } lon) return null;
        var located = legs.Where(l => l.Lat != 0 || l.Lon != 0).ToList();
        if (located.Count == 0) return null;
        return located.MinBy(l => GeoMath.HaversineKm(lat, lon, l.Lat, l.Lon)).Index;
    }

    public static TripWrongCity WrongCity(TripEnquiry e, TripPlan plan, DateOnly? start)
    {
        if (start is not { } s || TripSegmentRows.IsSegment(e) || (plan.Legs?.Count ?? 0) <= 1) return TripWrongCity.None;
        if (HomeLegIndex(e, plan) is not { } home) return TripWrongCity.None;

        var dayCount = plan.Days?.Count ?? 0;
        int wrong = 0, total = 0;
        int? first = null;
        foreach (var date in DatesOf(e))
        {
            if (DayOf(s, date, dayCount) is not { } day) continue;
            total++;
            if (plan.LegIndexOf(day) == home) continue;
            wrong++;
            first ??= day;
        }
        return wrong == 0
            ? new TripWrongCity(0, total, null, null)
            : new TripWrongCity(wrong, total, first, plan.LegOf(first!.Value)?.CityName);
    }

    public static bool OutsideTrip(TripEnquiry e, TripPlan plan, DateOnly? start)
    {
        if (start is not { } s || TripSegmentRows.IsSegment(e)) return false;
        var dayCount = plan.Days?.Count ?? 0;
        return DatesOf(e).Any(date => DayOf(s, date, dayCount) == null);
    }

    private static List<(int Index, string Name, double Lat, double Lon)> LegsOf(TripPlan plan)
    {
        if (plan.Legs is { Count: > 0 })
            return plan.Legs.Select((l, i) => (Leg: l, Index: i + 1)).Where(x => x.Leg != null)
                .Select(x => (x.Index, x.Leg.CityName, x.Leg.Latitude, x.Leg.Longitude)).ToList();
        return [(1, plan.CityName, plan.CentreLatitude ?? 0, plan.CentreLongitude ?? 0)];
    }

    private static int[]? ReadDays(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<int[]>(json); }
        catch (JsonException) { return null; }
    }
}

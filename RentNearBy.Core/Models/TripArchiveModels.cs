using System.Text;
using System.Text.Json;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// Trip compaction (owner order 2026-11-27, Buddy_resume/trip_compaction/SPEC.md + CONTRACT.md): 90 days after its end a
// finished trip keeps only this small summary (Trips.SummaryJson, under 2 KB); PlanJson becomes "{}".
// Serialized with TripJson.Options (camelCase, nulls omitted): a missing piece is simply not written.
public sealed class TripArchiveSummary
{
    public int Version { get; set; } = TripArchive.SummaryVersion;
    public string? Title { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? DayCount { get; set; }
    public int? SpotCount { get; set; }
    public List<TripArchiveCity> Cities { get; set; } = new();
    // Null (omitted) when no taxi was booked.
    public bool? TaxiBooked { get; set; }
    // Joined members plus the owner.
    public int? MemberCount { get; set; }
    // Whole rupees (TripExpense.Amount); null (omitted) when the trip has no expense.
    public long? ExpensesTotal { get; set; }
}

public sealed class TripArchiveCity
{
    public string Name { get; set; } = string.Empty;
    public int Nights { get; set; }
    // At most TripArchive.MaxSpotsPerCity names, in day order.
    public List<string> Spots { get; set; } = new();
    // Real number of distinct places in this city, so the app can show "+N more".
    public int? SpotCount { get; set; }
    // Confirmed stay names in this city.
    public List<string> Stays { get; set; } = new();
}

// What the builder reads besides the trip row: booked stay enquiries, whether a taxi was booked, joined members, expenses.
public sealed record TripArchiveInput(
    Trip Trip, IReadOnlyList<TripEnquiry> BookedStays, bool TaxiBooked, int JoinedMembers, long? ExpensesTotal);

public static class TripArchive
{
    public const int SummaryVersion = 1;
    public const int MaxBytes = 2048;
    public const int MaxSpotsPerCity = 6;
    public const int MaxStaysPerCity = 3;
    public const int MaxCities = 8;
    public const int NameMax = 60;
    public const string ArchivedMessage = "This trip is archived. Only its summary is kept.";
    // What PlanJson holds after compaction.
    public const string EmptyPlanJson = "{}";

    public static bool IsArchived(Trip trip) => trip.ArchivedAt != null;

    // Tolerant: null on empty, malformed or non-object JSON; never throws.
    public static TripArchiveSummary? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var summary = JsonSerializer.Deserialize<TripArchiveSummary>(json, TripJson.Options);
            if (summary == null) return null;
            summary.Cities = (summary.Cities ?? new List<TripArchiveCity>()).Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name)).ToList();
            foreach (var c in summary.Cities)
            {
                c.Spots = (c.Spots ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(MaxSpotsPerCity).ToList();
                c.Stays = (c.Stays ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                c.Nights = Math.Max(0, c.Nights);
            }
            return summary;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize(TripArchiveSummary summary) => JsonSerializer.Serialize(summary, TripJson.Options);

    public static int Bytes(TripArchiveSummary summary) => Encoding.UTF8.GetByteCount(Serialize(summary));

    // The route line of every list and card: the summary's cities for an archived trip (never empty), else the plan's legs.
    public static List<string> LegCities(Trip trip, TripPlan plan, TripArchiveSummary? summary)
    {
        if (IsArchived(trip))
        {
            var names = summary?.Cities.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>();
            return names.Count > 0 ? names : [trip.CityName];
        }
        return plan.Legs is { Count: > 0 } ? plan.LegCities : [trip.CityName];
    }

    internal static string? Cut(string? text, int max)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length > max ? t[..max].TrimEnd() : t;
    }
}

// Pure: never throws on a bad plan (TripPlanReader falls back to the row's city), never reads anything but its input.
public static class TripArchiveSummaryBuilder
{
    public static TripArchiveSummary Build(TripArchiveInput input)
    {
        var trip = input.Trip;
        var plan = TripPlanReader.Read(trip);
        var legs = (plan.Legs ?? new List<TripCityLeg>()).Take(TripArchive.MaxCities).ToList();
        var stayByLeg = StaysByLeg(input.BookedStays, plan, trip.StartDate);

        var cities = new List<TripArchiveCity>();
        foreach (var leg in legs)
        {
            var name = TripArchive.Cut(leg.CityName, TripArchive.NameMax) ?? (leg.Index == 1 ? TripArchive.Cut(trip.CityName, TripArchive.NameMax) : null);
            if (name == null) continue;
            var days = plan.DaysOf(leg.Index).OrderBy(d => d.Day).ToList();
            var spots = days
                .SelectMany(d => d.Spots ?? new List<TripSpot>())
                .Where(s => s != null)
                .Select(s => TripArchive.Cut(s.Name, TripArchive.NameMax))
                .Where(s => s != null)
                .Select(s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            cities.Add(new TripArchiveCity
            {
                Name = name,
                Nights = days.Count,
                Spots = spots.Take(TripArchive.MaxSpotsPerCity).ToList(),
                SpotCount = spots.Count > 0 ? spots.Count : null,
                Stays = stayByLeg.TryGetValue(leg.Index, out var stays) ? stays.Take(TripArchive.MaxStaysPerCity).ToList() : new List<string>(),
            });
        }

        var summary = new TripArchiveSummary
        {
            Version = TripArchive.SummaryVersion,
            Title = TripArchive.Cut(trip.Title, TripTitles.MaxLength),
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
            DayCount = trip.DayCount > 0 ? trip.DayCount : null,
            SpotCount = trip.SpotCount > 0 ? trip.SpotCount : null,
            Cities = cities,
            TaxiBooked = input.TaxiBooked ? true : null,
            MemberCount = 1 + Math.Max(0, input.JoinedMembers),
            ExpensesTotal = input.ExpensesTotal is > 0 ? input.ExpensesTotal : null,
        };
        Fit(summary);
        return summary;
    }

    // Keeps the summary under MaxBytes: fewer spot names first, then stay names, then the last cities.
    public static void Fit(TripArchiveSummary summary)
    {
        for (var keep = TripArchive.MaxSpotsPerCity - 1; keep >= 0 && TripArchive.Bytes(summary) > TripArchive.MaxBytes; keep--)
            foreach (var c in summary.Cities) if (c.Spots.Count > keep) c.Spots = c.Spots.Take(keep).ToList();
        for (var keep = TripArchive.MaxStaysPerCity - 1; keep >= 0 && TripArchive.Bytes(summary) > TripArchive.MaxBytes; keep--)
            foreach (var c in summary.Cities) if (c.Stays.Count > keep) c.Stays = c.Stays.Take(keep).ToList();
        while (summary.Cities.Count > 1 && TripArchive.Bytes(summary) > TripArchive.MaxBytes)
            summary.Cities.RemoveAt(summary.Cities.Count - 1);
        if (TripArchive.Bytes(summary) > TripArchive.MaxBytes) summary.Title = null;
    }

    // A booked stay belongs to the leg its context city names; else the leg of its check-in day; else leg 1 on a one-city trip.
    private static Dictionary<int, List<string>> StaysByLeg(IReadOnlyList<TripEnquiry> stays, TripPlan plan, DateOnly? start)
    {
        var map = new Dictionary<int, List<string>>();
        var legCount = plan.Legs?.Count ?? 1;
        foreach (var e in stays.Where(e => e != null).OrderBy(e => e.CheckIn).ThenBy(e => e.CreatedAt))
        {
            var name = TripArchive.Cut(e.ListingName, TripArchive.NameMax);
            if (name == null) continue;
            var leg = TripLegRules.HomeLegIndex(e, plan) is { } home && home >= 1 ? home : 0;
            if (leg == 0 && start is { } s && e.CheckIn is { } ci && TripLegRules.DayOf(s, ci, Math.Max(1, plan.Days?.Count ?? 0)) is { } day)
                leg = plan.LegIndexOf(day);
            if (leg == 0 && legCount == 1) leg = 1;
            if (leg == 0) continue;
            if (!map.TryGetValue(leg, out var list)) map[leg] = list = new List<string>();
            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
        }
        return map;
    }
}

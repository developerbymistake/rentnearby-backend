using System.Text.Json;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

public static class TripPlanReader
{
    // Always a normalised plan; bad JSON or a plan without a city falls back to the row's city, never zeros.
    public static TripPlan Read(Trip trip)
    {
        var plan = Parse(trip.PlanJson);
        plan.Normalize(trip.CityKey, trip.CityName, trip.CityLatitude, trip.CityLongitude, trip.DistrictId);
        return plan;
    }

    // Not normalised; a new TripPlan on bad JSON.
    public static TripPlan Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new TripPlan();
        try
        {
            var plan = JsonSerializer.Deserialize<TripPlan>(json, TripJson.Options) ?? new TripPlan();
            // A stored guide with an unknown step or choice keeps what is valid; the validator is what refuses it on save.
            plan.Guide = TripGuideRules.Sanitize(plan.Guide);
            return plan;
        }
        catch (JsonException)
        {
            return new TripPlan();
        }
    }
}

// A document written by a newer server is never overwritten.
public static class TripPlanCompat
{
    public static bool NeedsUpdate(int storedVersion) => storedVersion > TripPlan.CurrentVersion;
}

public static class TripTitles
{
    public const int MaxLength = 80;

    // "Haridwar trip", "Haridwar, Badrinath trip", or "Haridwar + 2 more trip" past 80 characters.
    public static string Default(IReadOnlyList<string> cityNames)
    {
        var names = cityNames.Select(n => (n ?? string.Empty).Trim()).Where(n => n.Length > 0).ToList();
        if (names.Count == 0) return "Trip";
        var full = $"{string.Join(", ", names)} trip";
        if (names.Count == 1 || full.Length <= MaxLength) return Cut(full);
        return Cut($"{names[0]} + {names.Count - 1} more trip");
    }

    private static string Cut(string text) => text.Length <= MaxLength ? text : text[..MaxLength];
}

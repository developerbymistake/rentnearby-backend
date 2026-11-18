using System.Globalization;
using System.Text;
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

public readonly record struct TripNameResult(string Title, bool NameChosen);

public static class TripNames
{
    public const int MaxLength = 60;
    public const string InvalidMessage = "Trip name must be 1 to 60 characters";

    // Trimmed, any space character becomes one space; null when empty, over 60 characters, without a letter or digit,
    // holding a link (the TripTextSafety rule) or an invisible character (control, format such as U+200B or U+202E,
    // line or paragraph separator, private use). A zero width joiner is kept only inside an emoji sequence.
    public static string? Clean(string? name)
    {
        if (name == null) return null;
        for (var i = 0; i < name.Length; i++)
        {
            if (Invisible(name, i)) return null;
        }
        var spaced = new string(name.Select(c => char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator ? ' ' : c).ToArray());
        var text = string.Join(' ', spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (text.Length is < 1 or > MaxLength) return null;
        if (!text.EnumerateRunes().Any(Rune.IsLetterOrDigit)) return null;
        return TripTextSafety.Clean(text, MaxLength) == text ? text : null;
    }

    private const char ZeroWidthJoiner = '\u200D';

    private static bool Invisible(string s, int i)
    {
        var c = s[i];
        switch (char.GetUnicodeCategory(c))
        {
            case UnicodeCategory.Control:
            case UnicodeCategory.LineSeparator:
            case UnicodeCategory.ParagraphSeparator:
            case UnicodeCategory.PrivateUse:
                return true;
            case UnicodeCategory.Format:
                return c != ZeroWidthJoiner || i == 0 || i == s.Length - 1 || !EmojiPart(s[i - 1]) || !EmojiPart(s[i + 1]);
            default:
                return false;
        }
    }

    // Halves of a surrogate pair (most emoji), symbols, and the variation selector U+FE0F.
    private static bool EmojiPart(char c)
        => char.IsSurrogate(c) || char.GetUnicodeCategory(c) is UnicodeCategory.OtherSymbol || c == '\uFE0F';

    // currentTitle null = a trip being created. A chosen name is never retitled by the server on its own; an explicit
    // non-empty title from an older build renames it under the name rules (cut to 60) and keeps NameChosen.
    public static TripNameResult Resolve(
        string? currentTitle, bool nameChosen, string? requestName, string? requestTitle,
        IReadOnlyList<string> storedCities, IReadOnlyList<string> newCities)
    {
        if (Clean(requestName) is { } name) return new TripNameResult(name, true);
        if (nameChosen && currentTitle != null)
            return new TripNameResult(Clean(TripTextSafety.Clean(requestTitle, MaxLength)) ?? currentTitle, true);
        if (requestTitle != null) return new TripNameResult(TitleOrDefault(requestTitle, newCities), false);
        if (currentTitle == null || currentTitle == TripTitles.Default(storedCities))
            return new TripNameResult(TripTitles.Default(newCities), false);
        return new TripNameResult(currentTitle, false);
    }

    private static string TitleOrDefault(string title, IReadOnlyList<string> cityNames)
    {
        var trimmed = TripTextSafety.Clean(title, TripTitles.MaxLength);
        return trimmed.Length > 0 ? trimmed : TripTitles.Default(cityNames);
    }
}

public static class TripInvites
{
    public const string ConfigKey = "AppLinks:PublicBaseUrl";

    // Host of the existing /app share page (Program.cs) and the verified Android App Link host.
    public const string DefaultPublicBaseUrl = "https://developerbymistake.tech";

    public static string PublicBaseUrl(string? configured)
        => Uri.TryCreate(configured?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? configured!.Trim()
            : DefaultPublicBaseUrl;

    public static string? Link(string? baseUrl, Guid code)
        => string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl.Trim().TrimEnd('/')}/app?invite={code}";
}

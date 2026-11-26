namespace RentNearBy.Core.Models.Social;

public sealed record WhenValue(string Kind, int Value);

// Pure rules of the social feature (18+, join gender, month-only projection, seats, join tier, names).
public static class SocialRules
{
    public const string WhenWithin = "within";
    public const string WhenMonth = "month";
    public const string WhenKindWithin = "within";
    public const string WhenKindExact = "exact";

    // (todayIST.Year * 12 + todayIST.Month) - (birthYear * 12 + birthMonth) >= 216.
    public static bool IsAdult(int birthYear, int birthMonth, DateOnly todayIst)
        => (todayIst.Year * 12 + todayIst.Month) - (birthYear * 12 + birthMonth) >= SocialLimits.AdultMonths;

    // The C# twin of PublicTripReader.LivePost: public, Draft/Planned, not started (IST day), inside its "within N months" window.
    public static bool IsLivePost(bool isPublic, string status, DateOnly? startDate, short? windowMonths, DateTime? publishedAtUtc, DateTime nowUtc)
        => isPublic && (status == TripStatuses.Draft || status == TripStatuses.Planned)
           && (startDate is null || startDate.Value >= IstTime.TodayIst(nowUtc))
           && (windowMonths is null || (publishedAtUtc is { } at && at.AddMonths(windowMonths.Value) >= nowUtc));

    public static bool IsValidBirth(int birthYear, int birthMonth)
        => birthMonth is >= 1 and <= 12 && birthYear is >= SocialLimits.BirthYearMin and <= SocialLimits.BirthYearMax;

    // M for Male, F for Female, null for Other (Other sees only Anyone posts).
    public static string? GenderCode(string? gender) => gender switch
    {
        SocialGenders.Male => JoinGenders.Men,
        SocialGenders.Female => JoinGenders.Women,
        _ => null,
    };

    // Spec 22: every public trip is visible to everyone; only the JOIN is gender-checked (Women only = women, Men only = men,
    // Other or no profile = Anyone posts only). The poster may pick any value whatever his own gender.
    public static bool CanJoin(string joinGender, string? viewerGender)
        => joinGender == JoinGenders.Anyone || (GenderCode(viewerGender) is { } code && code == joinGender);

    // Spec 22: only Women only needs Plus; Anyone and Men only are free.
    public static bool NeedsPlus(string joinGender) => joinGender == JoinGenders.Women;

    // Spec 25: accepted only when a time is stored and the stored version is the current one or newer.
    public static bool GuidelinesAccepted(DateTime? acceptedAt, int? version)
        => acceptedAt != null && version is { } v && v >= SocialLimits.GuidelinesVersion;

    public static bool IsValidSeats(int seats) => SocialLimits.SeatChoices.Contains(seats);

    public static int SeatsLeft(int seats, int joined) => Math.Max(0, seats - joined);

    public static short Tier(bool inOwnersBuddies, bool followsOwner) => inOwnersBuddies ? (short)0 : followsOwner ? (short)1 : (short)2;

    // The month-only projection for a non-member: a known month (exact date given, or the plan has a start date), else the
    // months left of the "roughly within N months" window, rounded up, at least 1.
    public static WhenValue When(DateOnly? startDate, int? postStartMonth, int? windowMonths, DateTime? publishedAtUtc, DateTime nowUtc)
    {
        if (startDate is { } d) return new WhenValue(WhenMonth, SocialMonth.Key(d));
        if (postStartMonth is { } m) return new WhenValue(WhenMonth, m);
        var months = windowMonths ?? 1;
        if (publishedAtUtc is { } published)
        {
            // Calendar months left until the window ends, a started month counted whole.
            var end = published.AddMonths(months);
            var left = (end.Year * 12 + end.Month) - (nowUtc.Year * 12 + nowUtc.Month);
            if (end.Day > nowUtc.Day || (end.Day == nowUtc.Day && end.TimeOfDay > nowUtc.TimeOfDay)) left += 1;
            months = left;
        }
        return new WhenValue(WhenWithin, Math.Max(1, months));
    }

    // Spec 23.9: when the user may change his name again; null = now (never changed since onboarding, or 14 days passed).
    public static DateTime? NameChangeAllowedAt(DateTime? nameChangedAt, DateTime nowUtc)
    {
        if (nameChangedAt is not { } last) return null;
        var next = last.AddDays(SocialLimits.NameChangeDays);
        return next > nowUtc ? next : null;
    }

    // Only a different name is a change (a save that sends the same name again never locks or refuses).
    public static bool IsNameChange(string? currentName, string newName) => !string.Equals(currentName?.Trim(), newName, StringComparison.Ordinal);

    public static string Initial(string? name)
    {
        var t = name?.Trim();
        return string.IsNullOrEmpty(t) ? "?" : char.ToUpperInvariant(t[0]).ToString();
    }

    public static string FirstName(string? name)
    {
        var t = name?.Trim();
        if (string.IsNullOrEmpty(t)) return "Someone";
        var space = t.IndexOf(' ');
        return space < 0 ? t : t[..space];
    }

    // Names on trip chat bubbles: first name + last initial ("Aman K").
    public static string BubbleName(string? name)
    {
        var t = name?.Trim();
        if (string.IsNullOrEmpty(t)) return "Someone";
        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1 ? parts[0] : $"{parts[0]} {char.ToUpperInvariant(parts[^1][0])}";
    }

    public static string Preview(string text, string? senderFirstName = null)
    {
        var line = senderFirstName == null ? text : $"{senderFirstName}: {text}";
        line = line.Replace('\n', ' ').Replace('\r', ' ');
        return line.Length <= SocialLimits.ChatPreviewMax ? line : line[..SocialLimits.ChatPreviewMax];
    }

    // A clean list value: trimmed, distinct, every value in the catalog, count inside [min, max].
    public static string[]? CatalogList(IReadOnlyList<string>? values, IReadOnlyList<string> catalog, int min, int max)
    {
        if (values is null) return null;
        var clean = values.Where(v => v != null).Select(v => v.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (clean.Length < min || clean.Length > max) return null;
        return clean.All(catalog.Contains) ? clean : null;
    }

    // Places: 1..20 items, each 2..40 characters, no 7+ digit run, never masked (a masked place is refused).
    // null = invalid shape; TextNotAllowed is told apart by the caller through PlaceHasContact.
    public static string[]? Places(IReadOnlyList<string>? values)
    {
        if (values is null) return null;
        var clean = values.Where(v => v != null).Select(v => ContactMask.StripInvisible(v).Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (clean.Length < SocialLimits.PlacesMin || clean.Length > SocialLimits.PlacesMax) return null;
        return clean.All(p => p.Length is >= SocialLimits.PlaceMinLength and <= SocialLimits.PlaceMaxLength) ? clean : null;
    }

    public static bool PlaceHasContact(string place) => ContactMask.HasDigitRun(place) || ContactMask.Apply(place).Masked;
}

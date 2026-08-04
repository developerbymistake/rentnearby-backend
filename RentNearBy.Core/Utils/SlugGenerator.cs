using System.Text;
using System.Text.RegularExpressions;

namespace RentNearBy.Core.Utils;

// Turns a ServiceCategory/Service Name into a URL-friendly slug for the public website
// (bakhli.com/services/{slug} instead of the raw Guid) — Google indexes hyphen-separated words in a
// URL as individual keywords, so "Tour & Travel" becoming "tour-travel" carries real search-keyword
// value that a Guid never could. Stop-words ("&", "and", "the", ...) are dropped rather than
// hyphenated in, since Google's own guidance is to keep slugs short/keyword-dense — every filler word
// is one more hyphen with no ranking value.
public static class SlugGenerator
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "of", "for", "to", "&",
    };

    private static readonly Regex NonAlphanumeric = new("[^a-z0-9]+", RegexOptions.Compiled);

    public static string Generate(string name)
    {
        var ascii = RemoveDiacritics(name.Trim().ToLowerInvariant());

        var words = ascii
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => NonAlphanumeric.Replace(w, ""))
            .Where(w => w.Length > 0 && !StopWords.Contains(w));

        var slug = string.Join('-', words);
        return slug.Length > 0 ? slug : "item";
    }

    // Appends -2, -3, ... until `taken` (existing slugs, lowercased) no longer contains the candidate —
    // called by the Create handlers, which fetch the small existing catalog and check in-memory rather
    // than needing a dedicated indexed lookup for what's realistically a handful of rows.
    public static string MakeUnique(string baseSlug, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(baseSlug)) return baseSlug;
        var i = 2;
        while (taken.Contains($"{baseSlug}-{i}")) i++;
        return $"{baseSlug}-{i}";
    }

    // Shared by RoomListing/PlotListing Create handlers: a pre-check-then-insert for slug uniqueness
    // would have the same TOCTOU gap the DB-level unique index exists to close, so both instead retry
    // the insert itself with a counter suffix. `setSlug` assigns the candidate onto the entity already
    // tracked by the caller; `trySaveAsync` must attempt to persist and return false (swallowing only
    // the unique-index collision) on failure — kept as a delegate rather than an EF Core call so this
    // dependency-free Core project never needs to reference EF Core/DbUpdateException directly.
    public static async Task<bool> GenerateUniqueSlugWithRetryAsync(
        string baseSlug,
        Action<string> setSlug,
        Func<Task<bool>> trySaveAsync,
        int maxAttempts = 5)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            setSlug(attempt == 1 ? baseSlug : $"{baseSlug}-{attempt}");
            if (await trySaveAsync()) return true;
        }
        return false;
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

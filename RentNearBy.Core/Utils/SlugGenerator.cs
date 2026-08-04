using System.Security.Cryptography;
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

    // Lowercase alphanumeric only (no confusable-character exclusion needed here, unlike
    // CouponCodeGenerator — slugs are read/shared via URL, not hand-typed from a support call).
    private const string SuffixAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int SuffixLength = 5;

    // Shared by RoomListing/PlotListing Create handlers: a pre-check-then-insert for slug uniqueness
    // would have the same TOCTOU gap the DB-level unique index exists to close, so both instead retry
    // the insert itself. Every attempt — including the first — appends a random 5-char suffix rather
    // than ever trying the bare base slug: at scale (millions of listings sharing a common
    // type+locality base, e.g. "3bhk-haldwani") a human-countable -2/-3/... counter would exhaust its
    // small attempt budget and start hard-failing listing creation for legitimate owners. A random
    // suffix gives ~60M combinations per distinct base slug, so real collisions are near-zero
    // probability regardless of how many listings share a base — the bounded retry below exists only
    // as defensive insurance against that vanishing case, not as the actual uniqueness mechanism.
    // `setSlug` assigns the candidate onto the entity already tracked by the caller; `trySaveAsync`
    // must attempt to persist and return false (swallowing only the unique-index collision) on
    // failure — kept as a delegate rather than an EF Core call so this dependency-free Core project
    // never needs to reference EF Core/DbUpdateException directly.
    public static async Task<bool> GenerateUniqueSlugWithRetryAsync(
        string baseSlug,
        Action<string> setSlug,
        Func<Task<bool>> trySaveAsync,
        int maxAttempts = 5)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            setSlug($"{baseSlug}-{RandomSuffix()}");
            if (await trySaveAsync()) return true;
        }
        return false;
    }

    // RandomNumberGenerator.GetInt32 uses rejection sampling internally and is unbiased, unlike
    // GetBytes(...) % SuffixAlphabet.Length, which would skew towards the low end of the alphabet
    // since 256 is not evenly divisible by 36 (mirrors CouponCodeGenerator.Generate's technique).
    private static string RandomSuffix()
    {
        var chars = new char[SuffixLength];
        for (var i = 0; i < SuffixLength; i++)
            chars[i] = SuffixAlphabet[RandomNumberGenerator.GetInt32(SuffixAlphabet.Length)];
        return new string(chars);
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

using System.Globalization;
using System.Text;

namespace RentNearBy.Core.Utils;

// Must stay identical to Atlas.Core.Models.SearchKeys.Fold.
public static class SearchKeys
{
    // NFKD, drop NonSpacingMark, lower, [^a-z0-9] -> space, collapse, trim.
    public static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark) continue;
            var lower = Rune.ToLowerInvariant(rune).Value;
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append((char)lower);
            }
            else
            {
                pendingSpace = true;
            }
        }
        return sb.ToString();
    }
}

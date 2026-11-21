using System.Text.RegularExpressions;

namespace RentNearBy.Core.Models.Owner;

// Doc 6.1.3: a run of 10 or more digits after removing spaces, dashes, dots, brackets, slashes, plus and underscore is a phone number.
// Any script's digits count (Devanagari, full-width). "Rs 25,000" passes (the comma stays).
public static partial class PhoneGuard
{
    [GeneratedRegex(@"[\s\-\.\(\)\[\]/+_]")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\p{Nd}{10,}")]
    private static partial Regex LongDigitRun();

    public static bool HasPhone(string? text)
        => !string.IsNullOrEmpty(text) && LongDigitRun().IsMatch(Separators().Replace(text, string.Empty));

    public static bool AnyHasPhone(params string?[] texts) => texts.Any(HasPhone);
}

using System.Text.RegularExpressions;

namespace RentNearBy.Core.Models.Owner;

// Doc 6.1.3: a run of 10 or more digits after removing spaces, dashes, dots and brackets is a phone number. "Rs 25,000" passes.
public static partial class PhoneGuard
{
    [GeneratedRegex(@"[\s\-\.\(\)\[\]]")]
    private static partial Regex Separators();

    [GeneratedRegex("[0-9]{10,}")]
    private static partial Regex LongDigitRun();

    public static bool HasPhone(string? text)
        => !string.IsNullOrEmpty(text) && LongDigitRun().IsMatch(Separators().Replace(text, string.Empty));

    public static bool AnyHasPhone(params string?[] texts) => texts.Any(HasPhone);
}

using System.Text.RegularExpressions;

namespace RentNearBy.Core.Models;

// Stay registration number, Udyam, GSTIN and "Other" document name rules; the user app mirrors them.
public static class StayRegistrationRules
{
    public const string OtherCode = "OTHER";
    public const string UdyamCode = "UDYAM";
    public const int CodeMax = 32;
    public const int TypeNameMax = 60;
    public const int HelpTextMax = 120;

    public const int RegistrationMin = 5;
    public const int RegistrationMax = 25;
    public const int OtherNameMin = 3;
    public const int OtherNameMax = 40;
    public const int GstinLength = 15;

    public const string RegistrationInvalidMessage = "Enter a valid registration number";
    public const string RegistrationRequiredMessage = "Enter your registration number";
    public const string RegistrationHelp = "5 to 25 letters, digits, / or -";
    public const string UdyamInvalidMessage = "Enter a valid Udyam number, e.g. UDYAM-UK-05-0001234";
    public const string GstinInvalidMessage = "Enter a valid GSTIN";
    public const string GstinHelp = "15 characters, e.g. 05ABCDE1234F1Z6";
    public const string OtherNameRequiredMessage = "Enter the document name";
    public const string OtherNameInvalidMessage = "Document name must be 3 to 40 characters";
    public const string TypeRequiredMessage = "Pick a registration type";
    public const string TypeInvalidMessage = "Pick a valid registration type";

    public static readonly Regex RegistrationPattern = new(@"^[A-Z0-9/-]{5,25}$", RegexOptions.CultureInvariant);
    public static readonly Regex UdyamPattern = new(@"^UDYAM-?([A-Z]{2})-?([0-9]{2})-?([0-9]{7})$", RegexOptions.CultureInvariant);
    public static readonly Regex UdyamCanonicalPattern = new(@"^UDYAM-([A-Z]{2})-([0-9]{2})-([0-9]{7})$", RegexOptions.CultureInvariant);
    public static readonly Regex GstinPattern = new(@"^([0-9]{2})([A-Z]{5}[0-9]{4}[A-Z])([1-9A-Z])(Z)([0-9A-Z])$", RegexOptions.CultureInvariant);
    public static readonly Regex OtherNamePattern = new(@"^[A-Za-z0-9 &/.'()-]{3,40}$", RegexOptions.CultureInvariant);

    private const string GstinAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex NonCodeRun = new(@"[^A-Z0-9]+", RegexOptions.CultureInvariant);

    public static string NormalizeGstin(string? raw) =>
        Spaces.Replace((raw ?? string.Empty).Trim().ToUpperInvariant(), string.Empty);

    public static bool IsValidGstin(string? normalised)
    {
        if (string.IsNullOrEmpty(normalised)) return false;
        var m = GstinPattern.Match(normalised);
        if (!m.Success) return false;
        var state = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (state < 1 || state > 38) return false;
        return GstinCheckChar(normalised[..14]) == normalised[14];
    }

    // Luhn mod 36 over the first 14 characters, factors 1,2,1,2,... from the left.
    public static char GstinCheckChar(string first14)
    {
        var sum = 0;
        for (var i = 0; i < first14.Length; i++)
        {
            var value = GstinAlphabet.IndexOf(first14[i]);
            if (value < 0) return '?';
            var product = value * (i % 2 == 0 ? 1 : 2);
            sum += product / 36 + product % 36;
        }
        return GstinAlphabet[(36 - sum % 36) % 36];
    }

    public static string NormalizeRegistration(string? raw) =>
        Spaces.Replace((raw ?? string.Empty).Trim().ToUpperInvariant(), string.Empty);

    public static bool IsValidRegistration(string? normalised) =>
        !string.IsNullOrEmpty(normalised) && RegistrationPattern.IsMatch(normalised) && normalised.Any(char.IsAsciiDigit);

    public static string NormalizeUdyam(string normalised)
    {
        var m = UdyamPattern.Match(normalised);
        return m.Success ? $"UDYAM-{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}" : normalised;
    }

    public static bool IsValidUdyam(string? canonical)
    {
        if (string.IsNullOrEmpty(canonical)) return false;
        var m = UdyamCanonicalPattern.Match(canonical);
        return m.Success && IndianStateCodes.Contains(m.Groups[1].Value);
    }

    public static bool IsUdyam(string? typeCode) => string.Equals(typeCode, UdyamCode, StringComparison.Ordinal);

    public static string NormalizeForType(string? typeCode, string? raw)
    {
        var n = NormalizeRegistration(raw);
        return IsUdyam(typeCode) ? NormalizeUdyam(n) : n;
    }

    public static bool IsValidForType(string? typeCode, string normalised) =>
        IsUdyam(typeCode) ? IsValidUdyam(NormalizeUdyam(normalised)) : IsValidRegistration(normalised);

    public static string MessageForType(string? typeCode) =>
        IsUdyam(typeCode) ? UdyamInvalidMessage : RegistrationInvalidMessage;

    public static string NormalizeOtherName(string? raw) => Spaces.Replace((raw ?? string.Empty).Trim(), " ");

    public static bool IsValidOtherName(string? normalised) =>
        !string.IsNullOrEmpty(normalised) && OtherNamePattern.IsMatch(normalised);

    public static string CodeOf(string? name)
    {
        var code = NonCodeRun.Replace((name ?? string.Empty).Trim().ToUpperInvariant(), "_").Trim('_');
        if (code.Length > CodeMax) code = code[..CodeMax].TrimEnd('_');
        return code;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace RentNearBy.Core.Models;

public enum DobStatus
{
    Ok,
    Future,
    Minor,
    TooOld,
}

// Driving licence (Sarathi 15-char form only) and date of birth rules; the user app mirrors them.
public static class TaxiIdentityRules
{
    public const int DlLength = 15;
    public const int DlEncMaxLength = 128;
    public const int DlMinYear = 1970;
    public const int MinAgeYears = 18;
    public const int MaxAgeYears = 100;
    public const int DlMinAgeAtIssue = 18;
    public const string DobFormat = "yyyy-MM-dd";

    public const string DlInvalidMessage = "Enter a valid licence number";
    public const string DlHelp = "As on your licence, e.g. UK07 20150012345";
    public const string DlOldHelp = "Old licence? Use the number shown on mParivahan";
    public const string DlRequiredMessage = "Enter your driving licence number";
    public const string DobInvalidMessage = "Enter a valid date of birth";
    public const string DobRequiredMessage = "Enter your date of birth";
    public const string DlYearMessage = "Licence year does not match your date of birth";
    public static readonly string DobMinorMessage = $"You must be {MinAgeYears} or older";

    public static readonly Regex DlPattern = new(@"^([A-Z]{2})([0-9]{2})([0-9]{4})([0-9]{7})$", RegexOptions.CultureInvariant);

    public static string NormalizeDl(string? raw) =>
        (raw ?? string.Empty).Trim().ToUpperInvariant()
            .Replace(" ", string.Empty).Replace("-", string.Empty).Replace("/", string.Empty).Replace(".", string.Empty);

    public static bool IsValidDl(string? normalised, int currentYear)
    {
        if (string.IsNullOrEmpty(normalised)) return false;
        var m = DlPattern.Match(normalised);
        if (!m.Success) return false;
        if (!IndianStateCodes.Contains(m.Groups[1].Value)) return false;
        if (m.Groups[2].Value == "00") return false;
        var year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (year < DlMinYear || year > currentYear) return false;
        return m.Groups[4].Value != "0000000";
    }

    public static int DlIssueYear(string normalised) =>
        int.Parse(normalised.Substring(4, 4), CultureInfo.InvariantCulture);

    public static string FormatDl(string normalised) =>
        normalised.Length == DlLength ? $"{normalised[..4]} {normalised[4..]}" : normalised;

    public static bool TryParseDob(string? value, out DateOnly dob)
    {
        dob = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return DateOnly.TryParseExact(value.Trim(), DobFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out dob);
    }

    public static string FormatDob(DateOnly dob) => dob.ToString(DobFormat, CultureInfo.InvariantCulture);

    public static int AgeOn(DateOnly dob, DateOnly today)
    {
        // Month/day compare, not AddYears: a 29 Feb birthday turns a year older on 1 March in non-leap years.
        var age = today.Year - dob.Year;
        if (today.Month < dob.Month || (today.Month == dob.Month && today.Day < dob.Day)) age--;
        return age;
    }

    public static DobStatus GetDobStatus(DateOnly dob, DateOnly today)
    {
        if (dob > today) return DobStatus.Future;
        var age = AgeOn(dob, today);
        if (age < MinAgeYears) return DobStatus.Minor;
        if (age > MaxAgeYears) return DobStatus.TooOld;
        return DobStatus.Ok;
    }

    public static bool DlYearMatchesDob(int issueYear, DateOnly dob) => issueYear >= dob.Year + DlMinAgeAtIssue;

    public static DateOnly TodayIst() => DateOnly.FromDateTime(DateTime.UtcNow + RoleChangeRules.IstOffset);
}

using System.Globalization;
using System.Security.Cryptography;

namespace RentNearBy.Core.Models;

// Server-made handle "Bakhli#" + 8 digits (first digit 1-9). Written once, never changed.
public static class Usernames
{
    public const string Prefix = "Bakhli#";
    public const int DigitCount = 8;
    public const int MaxLength = 24;
    public const int MaxAttempts = 6;
    public const int MinNumber = 10_000_000;
    public const int MaxNumberExclusive = 100_000_000;
    public const string UniqueIndex = "ix_users_username";
    public static readonly TimeSpan ReservationTtl = TimeSpan.FromHours(24);

    public static string Format(int number)
    {
        if (number < MinNumber || number >= MaxNumberExclusive)
            throw new ArgumentOutOfRangeException(nameof(number));
        return Prefix + number.ToString(CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string? text, out int number)
    {
        number = 0;
        if (text is null || text.Length != Prefix.Length + DigitCount || !text.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var digits = text.AsSpan(Prefix.Length);
        if (digits[0] < '1' || digits[0] > '9') return false;
        foreach (var c in digits)
            if (c < '0' || c > '9') return false;
        number = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    public static bool IsValid(string? text) => TryParse(text, out _);

    public static string NewRandom() => Format(RandomNumberGenerator.GetInt32(MinNumber, MaxNumberExclusive));

    // attempt returns the username that ended up stored, or null when the candidate clashed.
    public static async Task<string> AssignAsync(Func<string, Task<string?>> attempt, Func<string> next, string? preferred = null)
    {
        for (var i = 0; i < MaxAttempts; i++)
        {
            var candidate = i == 0 && IsValid(preferred) ? preferred! : next();
            var stored = await attempt(candidate);
            if (stored != null) return stored;
        }
        throw new UsernameUnavailableException();
    }
}

public sealed class UsernameUnavailableException()
    : Exception($"Could not create a unique username after {Usernames.MaxAttempts} tries.");

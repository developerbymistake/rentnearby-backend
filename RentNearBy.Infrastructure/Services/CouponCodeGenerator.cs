using System.Security.Cryptography;

namespace RentNearBy.Infrastructure.Services;

public static class CouponCodeGenerator
{
    // Excludes confusable characters (0/O, 1/I/L) — these codes are meant to be typed by hand.
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public static string Generate(int length = 8)
    {
        var chars = new char[length];
        // RandomNumberGenerator.GetInt32 uses rejection sampling internally and is unbiased, unlike
        // GetBytes(...) % Alphabet.Length, which would skew towards the low end of the alphabet
        // since 256 is not evenly divisible by 31.
        for (var i = 0; i < length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }
}

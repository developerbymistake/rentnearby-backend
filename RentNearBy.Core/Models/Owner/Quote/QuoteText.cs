using System.Text.RegularExpressions;

namespace RentNearBy.Core.Models.Owner.Quote;

// Text rules of the quotation master (ARCHITECTURE 3.3, 9.3.8): stored raw, never rendered by the server, but refused when it
// carries control characters (\n only where multi-line is allowed) or bidi / format characters that could reorder a printed line.
public static class QuoteText
{
    private static readonly Regex EmailShape = new(@"^[^@\s]{1,64}@[^@\s]+\.[A-Za-z]{2,}$", RegexOptions.CultureInvariant);
    private static readonly Regex Digits = new(@"^\d{9,18}$", RegexOptions.CultureInvariant);
    private static readonly Regex IfscShape = new(@"^[A-Z]{4}0[A-Z0-9]{6}$", RegexOptions.CultureInvariant);

    // U+200B-U+200F, U+202A-U+202E, U+2066-U+2069, U+FEFF.
    public static bool HasBidi(string? value)
    {
        if (value == null) return false;
        foreach (var c in value)
            if (c is >= '​' and <= '‏' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '﻿') return true;
        return false;
    }

    public static bool HasControl(string? value, bool allowNewline)
    {
        if (value == null) return false;
        foreach (var c in value)
        {
            if (allowNewline && c == '\n') continue;
            if (char.IsControl(c)) return true;
        }
        return false;
    }

    public static bool IsSafe(string? value, bool multiLine) => !HasBidi(value) && !HasControl(value, multiLine);

    // Trimmed; empty = null. "\r\n" becomes "\n" before any check.
    public static string? Clean(string? value)
    {
        if (value == null) return null;
        var v = value.Replace("\r\n", "\n").Trim();
        return v.Length == 0 ? null : v;
    }

    public static int LineCount(string? value) => value == null ? 0 : value.Split('\n').Length;

    public static bool IsValidEmail(string? value) => value != null && EmailShape.IsMatch(value);

    public static bool IsValidAccountNo(string? value) => value != null && Digits.IsMatch(value);

    public static bool IsValidIfsc(string? value) => value != null && IfscShape.IsMatch(value);

    // Display helpers of the hub summary: never the full account number or UPI.
    public static string? Last4(string? accountNo) => string.IsNullOrEmpty(accountNo) || accountNo.Length < 4 ? null : accountNo[^4..];

    public static string? MaskUpi(string? upi)
    {
        if (string.IsNullOrEmpty(upi)) return null;
        var at = upi.IndexOf('@');
        if (at <= 0) return "***";
        var head = upi[..Math.Min(2, at)];
        return head + "***" + upi[at..];
    }
}

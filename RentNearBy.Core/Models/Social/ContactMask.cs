using System.Text;
using System.Text.RegularExpressions;

namespace RentNearBy.Core.Models.Social;

// Server-side masking of phone numbers, emails, links and handles (contract 3.3). Only the masked text is ever stored.
public static partial class ContactMask
{
    public const string Mark = "•••• hidden";

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+\s*@\s*[A-Za-z0-9-]+(\s*\.\s*[A-Za-z]{2,})+")]
    private static partial Regex EmailRx();

    [GeneratedRegex(@"(https?://|www\.)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRx();

    [GeneratedRegex(@"\b[A-Za-z0-9-]+\.(com|in|net|org|me|co|io|app|link|ly|page|site)\b(/\S*)?", RegexOptions.IgnoreCase)]
    private static partial Regex DomainRx();

    [GeneratedRegex(@"(?<![A-Za-z0-9])@[A-Za-z0-9._]{3,}")]
    private static partial Regex HandleRx();

    // 7+ digits, consecutive digits at most 2 separator characters apart.
    [GeneratedRegex(@"\d(?:[\s\-.()+/]{0,2}\d){6,}")]
    private static partial Regex DigitsRx();

    private const string NumberWord =
        "zero|one|two|three|four|five|six|seven|eight|nine|ek|do|teen|char|paanch|chhe|saat|aath|nau|shunya|double|triple";

    [GeneratedRegex(@"\b(?:" + NumberWord + @")(?:[\s,\-]+(?:" + NumberWord + @")\b){5,}", RegexOptions.IgnoreCase)]
    private static partial Regex NumberWordsRx();

    // Same-length replacements on the matching copy (the email pattern allows spaces around @ and dots).
    private static readonly (string From, string To)[] Spelled =
    [
        (" at the rate ", "      @      "),
        ("(at)", " @  "),
        ("[at]", " @  "),
        (" dot ", "  .  "),
    ];

    public static (string Text, bool Masked) Apply(string? input)
    {
        if (string.IsNullOrEmpty(input)) return (string.Empty, false);
        var text = StripInvisible(input);
        var copy = MatchingCopy(text);

        var spans = new List<(int Start, int End)>();
        foreach (var rx in new[] { EmailRx(), LinkRx(), DomainRx(), HandleRx(), DigitsRx(), NumberWordsRx() })
            foreach (Match m in rx.Matches(copy))
                if (m.Length > 0) spans.Add((m.Index, m.Index + m.Length));
        if (spans.Count == 0) return (text, false);

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int End)>();
        foreach (var s in spans)
        {
            if (merged.Count > 0 && s.Start <= merged[^1].End + Gap(copy, merged[^1].End, s.Start))
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.End, s.End));
            }
            else merged.Add(s);
        }

        var sb = new StringBuilder(text.Length);
        var at = 0;
        foreach (var (start, end) in merged)
        {
            sb.Append(text, at, start - at);
            sb.Append(Mark);
            at = end;
        }
        sb.Append(text, at, text.Length - at);
        return (sb.ToString(), true);
    }

    // True when nothing but marks, spaces and punctuation is left (the field would carry no words).
    public static bool IsOnlyMarks(string masked)
        => masked.Replace(Mark, string.Empty).All(c => char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c));

    // A name or place with 7 or more digits in a row (after digit normalisation) is refused, never masked.
    public static bool HasDigitRun(string? input)
        => !string.IsNullOrEmpty(input) && DigitsRx().IsMatch(MatchingCopy(StripInvisible(input)));

    public static string StripInvisible(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
            if (!IsInvisible(c)) sb.Append(c);
        return sb.ToString();
    }

    private static bool IsInvisible(char c)
        => c is >= '​' and <= '‏' or >= '‪' and <= '‮' or >= '⁠' and <= '⁤'
            or >= '⁦' and <= '⁩' or '﻿' or '­';

    // Adjacent marks collapse: two matches with only spaces between them become one span.
    private static int Gap(string copy, int from, int to)
    {
        if (to <= from) return 0;
        for (var i = from; i < to; i++)
            if (!char.IsWhiteSpace(copy[i])) return 0;
        return to - from;
    }

    private static string MatchingCopy(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= '०' and <= '९') chars[i] = (char)('0' + (c - '०'));
            else if (c is >= '０' and <= '９') chars[i] = (char)('0' + (c - '０'));
            else if (c == '＠') chars[i] = '@';
        }
        for (var i = 1; i < chars.Length - 1; i++)
            if (chars[i] is 'o' or 'O' && char.IsAsciiDigit(chars[i - 1]) && char.IsAsciiDigit(chars[i + 1])) chars[i] = '0';

        var copy = new string(chars);
        foreach (var (from, to) in Spelled)
        {
            var idx = copy.IndexOf(from, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                copy = string.Concat(copy.AsSpan(0, idx), to, copy.AsSpan(idx + from.Length));
                idx = copy.IndexOf(from, idx + to.Length, StringComparison.OrdinalIgnoreCase);
            }
        }
        return copy;
    }
}

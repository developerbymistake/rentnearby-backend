using System.Globalization;

namespace RentNearBy.Core.Models.Social;

// Why a candidate is suggested. The numeric values are stored in the Redis snapshot hash, so never renumber them.
[Flags]
public enum DiscoverFlags
{
    None = 0,
    SameTrip = 1,
    TravelledWith = 2,
    FollowsYou = 4,
    Mutual = 8,
    Near = 16,
    Interests = 32,
    Places = 64,
}

public static class DiscoverReasonKinds
{
    public const string SameTrip = "sameTrip";
    public const string TravelledWith = "travelledWith";
    public const string FollowsYou = "followsYou";
    public const string Mutual = "mutual";
    public const string Near = "near";
    public const string Interests = "interests";
    public const string Places = "places";
}

public static class DiscoverChips
{
    public const string ForYou = "forYou";
    public const string SameTrip = "sameTrip";
    public const string Near = "near";
    public const string Mutual = "mutual";
    public const string Interests = "interests";

    public static readonly IReadOnlyList<string> All = [ForYou, SameTrip, Near, Mutual, Interests];

    public const string BadChipMessage = "Pick a filter from the list.";

    // Absent or empty = forYou; any other unknown value = false (400 BadChipMessage).
    public static bool TryParse(string? raw, out string chip)
    {
        chip = ForYou;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var match = All.FirstOrDefault(c => string.Equals(c, raw.Trim(), StringComparison.Ordinal));
        if (match == null) return false;
        chip = match;
        return true;
    }
}

// One scored candidate of the snapshot build (DESIGN 2.3).
public sealed record DiscoverCandidate(Guid UserId, DiscoverFlags Flags, int Points);

// A side of the "dates near" test: exact dates when known, else a month range (yyyymm keys, 0 = unknown).
public readonly record struct DiscoverSpan(DateOnly? Start, DateOnly? End, int FirstMonth, int LastMonth)
{
    public static DiscoverSpan Exact(DateOnly start, DateOnly end)
        => new(start, end < start ? start : end, SocialMonth.Key(start), SocialMonth.Key(end < start ? start : end));

    public static DiscoverSpan Months(int firstMonth, int lastMonth)
        => new(null, null, firstMonth, lastMonth < firstMonth ? firstMonth : lastMonth);

    public static readonly DiscoverSpan Unknown = new(null, null, 0, 0);

    public bool HasDates => Start.HasValue && End.HasValue;
    public bool HasMonths => FirstMonth > 0;
}

// Discover ranking and paging rules (Buddy_resume/discover/DESIGN.md 1.4, 2.1, 2.3, 3.1). Pure; the SQL lives in DiscoverService.
public static class DiscoverRules
{
    // Points per source ((proposal) weights, owner may change them here).
    public const int SameTripPoints = 100;
    public const int TravelledWithPoints = 70;
    public const int FollowsYouPoints = 50;
    public const int MutualPointsEach = 10;
    public const int MutualCountCap = 5;
    public const int NearPoints = 25;
    public const int InterestPointsEach = 8;
    public const int InterestCap = 3;
    public const int PlacePointsEach = 5;
    public const int PlaceCap = 3;
    public const int RecentBonus = 5;

    // Points fit in 10 bits; the tie fraction takes 40 bits (DESIGN 1.4).
    public const int MaxPoints = 1023;
    public const int TieBits = 40;

    public const int DatesSlackDays = 7;
    public const int MonthsSlack = 1;

    public static readonly TimeSpan HideFor = TimeSpan.FromDays(SocialLimits.DiscoverHideDays);

    // Private candidates may only come from these sources (they reveal nothing a locked profile hides).
    public const DiscoverFlags PrivateAllowed = DiscoverFlags.TravelledWith | DiscoverFlags.FollowsYou | DiscoverFlags.Mutual;

    // Order = tie order of the reason pick.
    private static readonly (DiscoverFlags Flag, string Kind)[] KindOrder =
    [
        (DiscoverFlags.SameTrip, DiscoverReasonKinds.SameTrip),
        (DiscoverFlags.TravelledWith, DiscoverReasonKinds.TravelledWith),
        (DiscoverFlags.FollowsYou, DiscoverReasonKinds.FollowsYou),
        (DiscoverFlags.Mutual, DiscoverReasonKinds.Mutual),
        (DiscoverFlags.Near, DiscoverReasonKinds.Near),
        (DiscoverFlags.Interests, DiscoverReasonKinds.Interests),
        (DiscoverFlags.Places, DiscoverReasonKinds.Places),
    ];

    public static int KindPoints(DiscoverFlags flag, int mutualCount, int sharedInterests, int sharedPlaces) => flag switch
    {
        DiscoverFlags.SameTrip => SameTripPoints,
        DiscoverFlags.TravelledWith => TravelledWithPoints,
        DiscoverFlags.FollowsYou => FollowsYouPoints,
        DiscoverFlags.Mutual => MutualPointsEach * Math.Clamp(mutualCount, 0, MutualCountCap),
        DiscoverFlags.Near => NearPoints,
        DiscoverFlags.Interests => InterestPointsEach * Math.Clamp(sharedInterests, 0, InterestCap),
        DiscoverFlags.Places => PlacePointsEach * Math.Clamp(sharedPlaces, 0, PlaceCap),
        _ => 0,
    };

    public static int Points(DiscoverFlags flags, int mutualCount, int sharedInterests, int sharedPlaces, bool recent = false)
    {
        var sum = KindOrder.Where(k => flags.HasFlag(k.Flag)).Sum(k => KindPoints(k.Flag, mutualCount, sharedInterests, sharedPlaces));
        if (recent && flags != DiscoverFlags.None) sum += RecentBonus;
        return Math.Min(sum, MaxPoints);
    }

    // The highest-points kind; ties follow the table order. Null when no flag is set.
    public static string? TopReason(DiscoverFlags flags, int mutualCount, int sharedInterests, int sharedPlaces)
    {
        string? best = null;
        var bestPoints = -1;
        foreach (var (flag, kind) in KindOrder)
        {
            if (!flags.HasFlag(flag)) continue;
            var p = KindPoints(flag, mutualCount, sharedInterests, sharedPlaces);
            if (p > bestPoints) { best = kind; bestPoints = p; }
        }
        return best;
    }

    // A chip page shows that chip's reason, not the top reason. forYou = top reason.
    public static string? ReasonFor(string chip, DiscoverFlags flags, int mutualCount, int sharedInterests, int sharedPlaces) => chip switch
    {
        DiscoverChips.SameTrip => flags.HasFlag(DiscoverFlags.SameTrip) ? DiscoverReasonKinds.SameTrip : null,
        DiscoverChips.Mutual => flags.HasFlag(DiscoverFlags.Mutual) ? DiscoverReasonKinds.Mutual : null,
        DiscoverChips.Near => flags.HasFlag(DiscoverFlags.Near) ? DiscoverReasonKinds.Near : null,
        DiscoverChips.Interests => flags.HasFlag(DiscoverFlags.Interests) ? DiscoverReasonKinds.Interests
            : flags.HasFlag(DiscoverFlags.Places) ? DiscoverReasonKinds.Places : null,
        _ => TopReason(flags, mutualCount, sharedInterests, sharedPlaces),
    };

    // The flags a chip filters on (forYou = every flag).
    public static DiscoverFlags ChipMask(string chip) => chip switch
    {
        DiscoverChips.SameTrip => DiscoverFlags.SameTrip,
        DiscoverChips.Mutual => DiscoverFlags.Mutual,
        DiscoverChips.Near => DiscoverFlags.Near,
        DiscoverChips.Interests => DiscoverFlags.Interests | DiscoverFlags.Places,
        _ => (DiscoverFlags)127,
    };

    public static bool Matches(DiscoverFlags flags, string chip) => (flags & ChipMask(chip)) != DiscoverFlags.None;

    // The flags a row may still show: a private candidate keeps only travelledWith / followsYou / mutual (None = drop the row).
    public static DiscoverFlags VisibleFlags(DiscoverFlags flags, bool isPrivate) => isPrivate ? flags & PrivateAllowed : flags;

    // DESIGN 2.1 "dates near": both exact = ranges overlap with 7 days of slack; otherwise month ranges equal or adjacent.
    // A side with neither dates nor a month (an undated plan) is near any date.
    public static bool DatesNear(DiscoverSpan a, DiscoverSpan b)
    {
        if (a.HasDates && b.HasDates)
            return a.Start!.Value <= b.End!.Value.AddDays(DatesSlackDays) && b.Start!.Value <= a.End!.Value.AddDays(DatesSlackDays);
        if (!a.HasMonths || !b.HasMonths) return true;
        return MonthIndex(a.FirstMonth) <= MonthIndex(b.LastMonth) + MonthsSlack
            && MonthIndex(b.FirstMonth) <= MonthIndex(a.LastMonth) + MonthsSlack;
    }

    // yyyymm -> months since year 0 (Dec 2026 and Jan 2027 are adjacent).
    public static int MonthIndex(int yyyymm) => yyyymm / 100 * 12 + (yyyymm % 100 - 1);

    public static int MonthFromIndex(int index) => index / 12 * 100 + index % 12 + 1;

    // The span of a public post: exact StartDate .. StartDate + PostDays - 1; else the month (PostStartMonth); else the
    // months covered by PostWindowMonths from PublishedAt (IST month of publish).
    public static DiscoverSpan PostSpan(DateOnly? startDate, short? postDays, int? postStartMonth, DateTime? publishedAt, short? windowMonths)
    {
        if (startDate is { } s) return DiscoverSpan.Exact(s, s.AddDays(Math.Max((postDays ?? 1) - 1, 0)));
        if (postStartMonth is int m && m > 0) return DiscoverSpan.Months(m, m);
        if (publishedAt is { } at && windowMonths is short w && w > 0)
        {
            var first = SocialMonth.Key(IstTime.TodayIst(at));
            return DiscoverSpan.Months(first, MonthFromIndex(MonthIndex(first) + w - 1));
        }
        return DiscoverSpan.Unknown;
    }

    // Unique zset score (DESIGN 1.4): points + (first 40 bits of the userId hex) / 2^40. Exact in a double.
    public static double ZScore(int points, Guid userId) => Math.Clamp(points, 0, MaxPoints) + Tie(userId);

    public static double Tie(Guid userId)
    {
        var bits = ulong.Parse(userId.ToString("N")[..10], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bits / (double)(1UL << TieBits);
    }

    // The snapshot rows (DESIGN 2.3): the top ChipQuota of each chip flag first, deduped, then the rest by score up to Max.
    // Returned in score order (zscore DESC).
    public static IReadOnlyList<DiscoverCandidate> SelectSnapshot(IEnumerable<DiscoverCandidate> candidates,
        int max = SocialLimits.DiscoverSnapshotMax, int chipQuota = SocialLimits.DiscoverChipQuota)
    {
        var ordered = candidates
            .GroupBy(c => c.UserId).Select(g => g.OrderByDescending(c => c.Points).First())
            .OrderByDescending(c => ZScore(c.Points, c.UserId)).ToList();
        var picked = new HashSet<Guid>();
        var result = new List<DiscoverCandidate>();
        foreach (var chip in new[] { DiscoverChips.SameTrip, DiscoverChips.Mutual, DiscoverChips.Near, DiscoverChips.Interests })
            foreach (var c in ordered.Where(c => Matches(c.Flags, chip)).Take(chipQuota))
                if (result.Count < max && picked.Add(c.UserId)) result.Add(c);
        foreach (var c in ordered)
        {
            if (result.Count >= max) break;
            if (picked.Add(c.UserId)) result.Add(c);
        }
        return result.OrderByDescending(c => ZScore(c.Points, c.UserId)).ToList();
    }
}

// The compact value of one snapshot row in the Redis hash (DESIGN 3.1):
// "flags|points|mutualCount|mutualFirstId|cityKey|month|interestIdx|placeIdx" (ids as N, empty = none, idx = digits of the
// candidate's Interests / Places array positions). Names and city names are resolved live by the hydrate.
public sealed record DiscoverRowCode(
    DiscoverFlags Flags, int Points, int MutualCount, Guid? MutualFirstId, long? CityKey, int? Month,
    IReadOnlyList<int> InterestIdx, IReadOnlyList<int> PlaceIdx)
{
    public string Encode() => string.Join('|',
        ((int)Flags).ToString(CultureInfo.InvariantCulture),
        Points.ToString(CultureInfo.InvariantCulture),
        MutualCount.ToString(CultureInfo.InvariantCulture),
        MutualFirstId?.ToString("N") ?? string.Empty,
        CityKey?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        Month?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        Digits(InterestIdx),
        Digits(PlaceIdx));

    public static bool TryDecode(string? raw, out DiscoverRowCode row)
    {
        row = null!;
        if (string.IsNullOrEmpty(raw)) return false;
        var p = raw.Split('|');
        if (p.Length != 8) return false;
        if (!int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var flags)
            || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var points)
            || !int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var mutual)) return false;
        Guid? first = null;
        if (p[3].Length > 0) { if (!Guid.TryParseExact(p[3], "N", out var g)) return false; first = g; }
        long? city = null;
        if (p[4].Length > 0) { if (!long.TryParse(p[4], NumberStyles.None, CultureInfo.InvariantCulture, out var c)) return false; city = c; }
        int? month = null;
        if (p[5].Length > 0) { if (!int.TryParse(p[5], NumberStyles.None, CultureInfo.InvariantCulture, out var m)) return false; month = m; }
        if (!p[6].All(char.IsAsciiDigit) || !p[7].All(char.IsAsciiDigit)) return false;
        row = new DiscoverRowCode((DiscoverFlags)flags, points, mutual, first, city, month,
            p[6].Select(ch => ch - '0').ToArray(), p[7].Select(ch => ch - '0').ToArray());
        return true;
    }

    private static string Digits(IReadOnlyList<int> idx) => string.Concat(idx.Where(i => i is >= 0 and <= 9).Select(i => (char)('0' + i)));
}

// Redis keys of one user's Discover snapshot ({guid} hash tag: one cluster slot, so MULTI over them is safe).
public static class DiscoverKeys
{
    public const string Prefix = "discover:";
    public const long SnapshotTtlMs = 6 * 60 * 60 * 1000;
    public const long LockTtlMs = 5000;
    public const long RefreshGapMs = 30_000;

    public static string Tag(Guid userId) => $"{{{userId:N}}}";
    public static string Scores(Guid userId) => $"{Prefix}{Tag(userId)}:z";
    public static string Rows(Guid userId) => $"{Prefix}{Tag(userId)}:r";
    public static string Stamp(Guid userId) => $"{Prefix}{Tag(userId)}:s";
    public static string Lock(Guid userId) => $"{Prefix}lock:{Tag(userId)}";
    public static string Refresh(Guid userId) => $"{Prefix}rf:{Tag(userId)}";

    // The snapshot itself (invalidation DEL).
    public static string[] Snapshot(Guid userId) => [Scores(userId), Rows(userId), Stamp(userId)];

    // zset member = userId as 32 hex chars.
    public static string Member(Guid userId) => userId.ToString("N");
}

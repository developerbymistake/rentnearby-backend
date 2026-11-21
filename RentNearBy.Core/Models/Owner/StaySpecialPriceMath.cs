namespace RentNearBy.Core.Models.Owner;

// Only active rules are passed: not paused, not paused by plan. Rules of one stay never overlap.
public readonly record struct PriceRule(DateOnly From, DateOnly To, bool Lower, int Percent);

// Contract 8.2 and vectors/special_price_vectors.json (version 2). The one price function: every read that shows a dated stay price calls it.
public static class StaySpecialPriceMath
{
    public const int MinPrice = 100;
    public const int MaxPrice = 9_900_000;

    // raw = base * (100 -/+ percent); ((raw + 500) / 1000) * 10; clamp 100..9900000.
    public static int Adjust(int basePrice, bool lower, int percent)
    {
        long raw = (long)basePrice * (lower ? 100 - percent : 100 + percent);
        var rounded = (raw + 500) / 1000 * 10;
        return (int)Math.Clamp(rounded, MinPrice, MaxPrice);
    }

    public static int PriceOn(int basePrice, DateOnly night, IReadOnlyList<PriceRule> rules)
    {
        foreach (var r in rules)
            if (r.From <= night && night <= r.To) return Adjust(basePrice, r.Lower, r.Percent);
        return basePrice;
    }

    // Nights checkIn .. checkIn + nights - 1; the check-out day is not a night.
    public static int Total(int basePrice, DateOnly checkIn, int nights, IReadOnlyList<PriceRule> rules)
    {
        long total = 0;
        for (var i = 0; i < nights; i++) total += PriceOn(basePrice, checkIn.AddDays(i), rules);
        return (int)Math.Min(total, int.MaxValue);
    }

    // Total / nights rounded to the nearest Rs 10; a .5 tie rounds up.
    public static int PerNight(int total, int nights)
    {
        if (nights <= 0) return total;
        return (int)(((long)total + 5L * nights) / (10L * nights) * 10);
    }

    // "N% off" only for a Lower rule covering a night and only when the signed percents of the covering rules sum below 0
    // (the dated total is below the base total whatever the room price). The biggest Lower percent wins.
    public static int? LowerPercentFor(DateOnly checkIn, int nights, IReadOnlyList<PriceRule> rules)
    {
        var signed = 0;
        int? best = null;
        for (var i = 0; i < nights; i++)
        {
            var night = checkIn.AddDays(i);
            foreach (var r in rules)
            {
                if (night < r.From || night > r.To) continue;
                if (r.Lower)
                {
                    signed -= r.Percent;
                    best = Math.Max(best ?? 0, r.Percent);
                }
                else
                {
                    signed += r.Percent;
                }
            }
        }
        return best is not null && signed < 0 ? best : null;
    }

    // Blocks hold inclusive nights; true when one touches checkIn .. checkIn + nights - 1.
    public static bool TouchesBlock(DateOnly checkIn, int nights, IReadOnlyList<(DateOnly From, DateOnly To)> blocks)
    {
        if (nights <= 0) return false;
        var last = checkIn.AddDays(nights - 1);
        foreach (var b in blocks)
            if (b.From <= last && b.To >= checkIn) return true;
        return false;
    }

    // Stays tab card chip (T3): the active Lower rule with to >= today and from <= today + 30 that starts first (tie: bigger percent).
    public static int? CardChipPercent(DateOnly today, IReadOnlyList<PriceRule> rules)
    {
        var horizon = today.AddDays(OwnerLimits.StaysChipWithinDays);
        PriceRule? pick = null;
        foreach (var r in rules)
        {
            if (!r.Lower || r.To < today || r.From > horizon) continue;
            if (pick is null || r.From < pick.Value.From || (r.From == pick.Value.From && r.Percent > pick.Value.Percent)) pick = r;
        }
        return pick?.Percent;
    }

    public static string ChipText(int percent) => $"{percent}% off";
}

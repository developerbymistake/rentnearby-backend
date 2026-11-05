namespace RentNearBy.Core.Models;

// One switch ledger: list = discount + creditApplied + payable. The credit covers what the coupon leaves; any
// credit above that becomes extra days of the new plan at its list rate, up to the remaining-days cap.
public readonly record struct SwitchProration(
    int CreditValueInr,
    int CreditAppliedInr,
    int PayableInr,
    int BaseDays,
    int BonusDays,
    int BonusValueInr,
    int ForfeitedInr)
{
    public int GrantedDays => BaseDays + BonusDays;
    public int CarriedValueInr => CreditAppliedInr + BonusValueInr;
}

public static class PlanProration
{
    // Unused prepaid value at asOf: paid-per-day times the exact time left, floored to whole rupees.
    public static int CreditValueInr(int totalPaidInr, int totalDays, DateTime endsAtUtc, DateTime asOfUtc)
    {
        if (totalPaidInr <= 0 || totalDays <= 0 || endsAtUtc <= asOfUtc) return 0;
        var remainingTicks = (decimal)(endsAtUtc - asOfUtc).Ticks;
        var totalTicks = (decimal)totalDays * TimeSpan.TicksPerDay;
        var value = Math.Floor(totalPaidInr * remainingTicks / totalTicks);
        return (int)Math.Min(totalPaidInr, value);
    }

    public static SwitchProration Switch(int listInr, int discountInr, int creditValueInr, int baseDays, int maxTotalDays)
    {
        listInr = Math.Max(0, listInr);
        discountInr = Math.Clamp(discountInr, 0, listInr);
        creditValueInr = Math.Max(0, creditValueInr);
        baseDays = Math.Max(0, baseDays);

        var net = listInr - discountInr;
        var applied = Math.Min(creditValueInr, net);
        var excess = creditValueInr - applied;
        var bonusDays = 0;
        if (excess > 0 && listInr > 0 && baseDays > 0)
        {
            var room = Math.Max(0, maxTotalDays - baseDays);
            bonusDays = (int)Math.Min(room, (long)excess * baseDays / listInr);
        }
        var bonusValue = BonusValueInr(listInr, baseDays, bonusDays);
        return new SwitchProration(creditValueInr, applied, net - applied, baseDays, bonusDays, bonusValue, excess - bonusValue);
    }

    // Value of extra days at the new plan's list rate, floored (never more than the credit that bought them).
    public static int BonusValueInr(int listInr, int baseDays, int bonusDays)
        => listInr <= 0 || baseDays <= 0 || bonusDays <= 0 ? 0 : (int)((long)bonusDays * listInr / baseDays);

    // Prepaid value a switch purchase moved onto the new plan (credit applied plus the bonus days' value).
    public static int CarriedValueInr(int creditInr, int listInr, int baseDays, int grantedDays)
        => Math.Max(0, creditInr) + BonusValueInr(listInr, baseDays, Math.Max(0, grantedDays - baseDays));
}

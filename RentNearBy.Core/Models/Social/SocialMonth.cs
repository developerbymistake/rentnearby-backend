namespace RentNearBy.Core.Models.Social;

// Calendar month in IST: every monthly cap counts rows created at or after StartUtc.
public static class SocialMonth
{
    public static DateTime StartUtc(DateTime nowUtc)
    {
        var today = IstTime.TodayIst(nowUtc);
        return IstTime.IstMidnightUtc(new DateOnly(today.Year, today.Month, 1));
    }

    public static DateTime ResetsAtUtc(DateTime nowUtc)
    {
        var today = IstTime.TodayIst(nowUtc);
        return IstTime.IstMidnightUtc(new DateOnly(today.Year, today.Month, 1).AddMonths(1));
    }

    // yyyymm of a date.
    public static int Key(DateOnly date) => date.Year * 100 + date.Month;
}

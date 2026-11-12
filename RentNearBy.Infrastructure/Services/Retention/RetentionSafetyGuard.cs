namespace RentNearBy.Infrastructure.Services.Retention;

// Pure: a rule may not delete more than MaxPercent of its table in one run.
public static class RetentionSafetyGuard
{
    public static bool Applies(long tableRows, int guardMinRows, bool exempt, bool allowLargeBacklog)
        => !exempt && !allowLargeBacklog && tableRows >= guardMinRows;

    public static long Cap(long tableRows, int maxPercent) => tableRows * maxPercent / 100;

    public static bool IsOverCap(long candidates, long cap) => candidates > cap;
}

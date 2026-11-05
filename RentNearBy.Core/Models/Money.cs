namespace RentNearBy.Core.Models;

// INR amounts: paise are exact, rupees are derived with one rounding rule (half away from zero).
public static class Money
{
    public const long MicrosPerPaisa = 10_000;
    public const long PaisePerRupee = 100;

    public static long PaiseFromMicros(long micros)
        => (long)Math.Round(micros / (decimal)MicrosPerPaisa, MidpointRounding.AwayFromZero);

    public static long PaiseFromRupees(int rupees) => rupees * PaisePerRupee;

    public static int RupeesFromPaise(long paise)
        => (int)Math.Round(paise / (decimal)PaisePerRupee, MidpointRounding.AwayFromZero);

    // Rounded once from micros (never micros -> paise -> rupees, which double-rounds 149.499999 up to 150).
    public static int RupeesFromMicros(long micros)
        => (int)Math.Round(micros / 1_000_000m, MidpointRounding.AwayFromZero);

    // Google's INR price in paise, null for another currency or no price.
    public static long? InrPaiseOf(long? micros, string? currency)
        => currency == "INR" && micros is > 0 ? PaiseFromMicros(micros.Value) : null;

    // What a charge row really took: the captured paise when known, else its rupee amount.
    public static long ChargedPaise(long? capturedPaise, int payableInr)
        => capturedPaise ?? PaiseFromRupees(payableInr);
}

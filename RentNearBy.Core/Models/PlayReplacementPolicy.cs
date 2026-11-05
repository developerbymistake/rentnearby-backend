namespace RentNearBy.Core.Models;

// D-07: inside one product only chargeFullPrice (longer cycle) or withoutProration; across products a lower
// per-month price is a deferred downgrade, anything else an immediate charge. Prices are compared as exact
// integer cross products (no doubles); Google's price wins over the admin mirror.
public static class PlayReplacementPolicy
{
    public static string Decide(bool sameProduct, int currentCycleMonths, int newCycleMonths, long currentPriceMicros, long newPriceMicros)
    {
        var currentCycle = Math.Max(1, currentCycleMonths);
        var newCycle = Math.Max(1, newCycleMonths);
        if (sameProduct)
            return newCycle > currentCycle ? PlayReplacementModes.ChargeFullPrice : PlayReplacementModes.WithoutProration;
        var newPerMonth = (Int128)newPriceMicros * currentCycle;
        var currentPerMonth = (Int128)currentPriceMicros * newCycle;
        return newPerMonth >= currentPerMonth ? PlayReplacementModes.ChargeFullPrice : PlayReplacementModes.Deferred;
    }

    // Google's latest INR price when one was seen, else the mirror in rupees.
    public static long PriceMicros(long? googleInrMicros, int mirrorInr)
        => googleInrMicros is > 0 ? googleInrMicros.Value : mirrorInr * 1_000_000L;
}

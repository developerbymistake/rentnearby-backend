using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// Route fares, the day rate and the km per day are all compulsory before a taxi can go live.
public static class TaxiRateRules
{
    public const string SubmitMessage = "Add your day rate and km before you submit";
    public const string RateMissing = "Add your day rate";
    public const string KmMissing = "Add the km included per day";
    public const string RateRange = "Day rate must be between ₹100 and ₹99,00,000";
    public const string KmRange = "Km per day must be 1 to 1000";

    public static bool RateOk(int? rate) => rate is >= TaxiLimits.MinFare and <= TaxiLimits.MaxFare;
    public static bool KmOk(int? km) => km is >= 1 and <= TaxiLimits.LocalDayKmMax;

    public static bool Done(TaxiListing t) =>
        RateOk(t.LocalDayRate) && KmOk(t.LocalDayKm)
        && t.Routes.Count >= 1 && t.Routes.All(r => r.Fare is >= TaxiLimits.MinFare and <= TaxiLimits.MaxFare);

    public static string? Error(UpdateTaxiLocalRateRequest r)
    {
        if (r.DayRate == null) return RateMissing;
        if (!RateOk(r.DayRate)) return RateRange;
        if (r.KmPerDay == null) return KmMissing;
        if (!KmOk(r.KmPerDay)) return KmRange;
        return null;
    }
}

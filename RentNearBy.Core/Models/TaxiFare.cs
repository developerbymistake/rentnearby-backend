namespace RentNearBy.Core.Models;

// The price of a ridden part of a route. A driver quotes one fare for his whole route; a stretch of it is
// estimated by its share of the road length, never below the minimum fare and never above the quote.
public static class TaxiFare
{
    public const int RoundTo = 50;
    private const double Whole = 1 - 1e-9;

    public static bool IsPart(double span) => span < Whole;

    public static int ForSpan(int routeFare, double span, int minFare = TaxiLimits.MinFare)
    {
        if (routeFare <= 0) return routeFare;
        if (double.IsNaN(span) || !IsPart(span)) return routeFare;
        var share = Math.Max(0, span) * routeFare;
        var rounded = (int)(Math.Round(share / RoundTo, MidpointRounding.AwayFromZero) * RoundTo);
        return Math.Min(routeFare, Math.Max(Math.Min(minFare, routeFare), rounded));
    }

    // Span of a link: the road-length fraction between its two fractions (whole route = 1).
    public static double SpanOf(double fromFraction, double toFraction) =>
        Math.Clamp(Math.Abs(toFraction - fromFraction), 0, 1);
}

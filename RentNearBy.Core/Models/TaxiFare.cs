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

    // Where a traveller point sits on a route, by the rule the ways network links with (TaxiWaysClustering): within endMeters
    // of the route's own end point = that end (the nearer one), else its projection on the road line within alongMeters.
    // Null = not on this route.
    public static double? FractionOnRoute(
        IReadOnlyList<TaxiWaysPoint> line, double[] cumulative, TaxiWaysPoint routeFrom, TaxiWaysPoint routeTo, TaxiWaysPoint p,
        double endMeters, double alongMeters)
    {
        var dFrom = TaxiWaysGeo.DistanceMeters(p, routeFrom);
        var dTo = TaxiWaysGeo.DistanceMeters(p, routeTo);
        if (dFrom <= endMeters || dTo <= endMeters) return dFrom <= dTo ? 0 : 1;
        if (line.Count < 2) return null;
        var (meters, fraction) = TaxiWaysGeo.PointToPolyline(p, line, cumulative);
        return meters <= alongMeters ? fraction : null;
    }

    // Span of the stretch a -> b on a route (whole route = 1); null when an end is not on it or both ends are the same point of it.
    public static double? SpanOnRoute(
        IReadOnlyList<TaxiWaysPoint> line, TaxiWaysPoint routeFrom, TaxiWaysPoint routeTo, TaxiWaysPoint a, TaxiWaysPoint b,
        double endMeters, double alongMeters)
    {
        if (line.Count >= 2 && TaxiWaysGeo.DistanceMeters(line[0], routeTo) < TaxiWaysGeo.DistanceMeters(line[0], routeFrom))
            line = line.Reverse().ToList();
        var cumulative = line.Count >= 2 ? TaxiWaysGeo.Cumulative(line) : [];
        if (FractionOnRoute(line, cumulative, routeFrom, routeTo, a, endMeters, alongMeters) is not { } fa
            || FractionOnRoute(line, cumulative, routeFrom, routeTo, b, endMeters, alongMeters) is not { } fb)
            return null;
        var span = SpanOf(fa, fb);
        return span <= 0 ? null : span;
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

public class TripPlannerService(IOsrmClient osrm, ITripRoadCache roadCache, IOptions<TripPlannerOptions> options, ILogger<TripPlannerService> logger) : ITripPlannerService
{
    public async Task<TripPlanResultDto> PlanAsync(TripCityInfo city, PlanTripRequest request, CancellationToken ct = default)
    {
        var o = options.Value;
        var limit = request.DayLimitMinutes ?? o.DayLimitMinutes;
        var cap = Math.Clamp(o.MaxDays, 1, TripLimits.MaxDays);
        var maxDays = Math.Clamp(request.MaxDays ?? o.MaxDays, 1, cap);
        var recalculate = request.Mode == TripPlanModes.Recalculate;

        var dayInputs = recalculate ? request.Days ?? new List<PlanDayInput>() : new List<PlanDayInput>();
        var days = dayInputs.Select(d => (d.Day, Spots: d.Spots.Select(ToSpot).ToList())).ToList();
        var allSpots = (recalculate
                ? days.SelectMany(d => d.Spots)
                : (request.Spots ?? new List<PlanSpotInput>()).Select(ToSpot))
            .DistinctBy(s => s.Id)
            .ToList();

        var centreLat = city.Latitude;
        var centreLon = city.Longitude;
        var anchor = TripAnchor.Spot(request.AnchorLatitude, request.AnchorLongitude);
        var points = new DayPoints(allSpots.Count);
        var anchorIndex = points.Add(anchor);
        var ends = recalculate
            ? dayInputs.Select(d => new DayEnds(
                    points.Add(TripAnchor.Spot(d.StartLatitude, d.StartLongitude)) is var a and >= 0 ? a : anchorIndex,
                    points.Add(TripAnchor.Spot(d.EndLatitude, d.EndLongitude)) is var b and >= 0 ? b : anchorIndex,
                    d.BestOrder))
                .ToList()
            : null;
        var matrixSpots = points.Count == 0 ? allSpots : allSpots.Concat(points.Spots).ToList();

        RoadMatrix? raw = null;
        if (matrixSpots.Count >= 2 && osrm.IsConfigured)
            raw = points.Count == 0
                ? await CityMatrixSliceAsync(city, allSpots, ct) ?? await osrm.TableAsync(Points(allSpots), ct)
                : await osrm.TableAsync(Points(matrixSpots), ct) ?? TripAnchor.Embed(await CityMatrixSliceAsync(city, allSpots, ct), matrixSpots.Count);
        var matrix = DaySplitter.FillEstimates(matrixSpots, raw, o.FallbackSpeedKmh, o.EstimateFactor);
        var osrmAvailable = raw != null;

        var warnings = new List<TripWarningDto>();
        if (!osrmAvailable && matrixSpots.Count >= 2)
            warnings.Add(new TripWarningDto { Code = TripWarningCodes.OsrmUnavailable, Message = "Road times are estimates" });

        var firstDayUsed = Math.Clamp(request.FirstDayUsedMinutes ?? 0, 0, 900);
        var result = recalculate
            ? DaySplitter.Recalculate(days, limit, request.StartDate, matrix, allSpots, firstDayUsed, null, ends)
            : DaySplitter.Split(new SplitInput(allSpots, centreLat, centreLon, limit, request.StartDate, maxDays, firstDayUsed), matrix, anchor);

        warnings.AddRange(result.Warnings.Select(w => new TripWarningDto { Code = w.Code, Message = w.Message, SpotId = w.SpotId, Day = w.Day }));

        var dto = new TripPlanResultDto
        {
            CentreLatitude = centreLat,
            CentreLongitude = centreLon,
            OsrmAvailable = osrmAvailable,
            DayLimitMinutes = limit,
        };

        var routes = new List<(TripPlanDayDto Day, List<(double Lat, double Lon)> Points)>();
        for (var k = 0; k < result.Days.Count; k++)
        {
            var day = result.Days[k];
            var dayDto = new TripPlanDayDto
            {
                Day = day.Day,
                SpotIds = day.Spots.Select(s => s.Id).ToList(),
                VisitMinutes = day.VisitMinutes,
                DriveMinutes = day.DriveMinutes,
                TotalMinutes = day.TotalMinutes,
                Km = day.Km,
                OverLimit = day.OverLimit,
                IsEstimate = day.IsEstimate,
                Legs = day.Legs.Select(l => new TripLegDto { FromId = l.FromId, ToId = l.ToId, Minutes = l.Minutes, Km = l.Km, IsEstimate = l.IsEstimate }).ToList(),
            };

            var start = ends == null ? anchor : points.At(ends[k].Start);
            var end = ends == null ? anchor : points.At(ends[k].End);
            var route = RoutePoints(day.Spots, start, end);
            if (osrmAvailable && route.Count >= 2 && routes.Count < o.RouteGeometryMaxDays)
                routes.Add((dayDto, route));

            dto.Days.Add(dayDto);
        }

        var geometries = await roadCache.GetRouteGeometriesAsync(routes.Select(r => (IReadOnlyList<(double Lat, double Lon)>)r.Points).ToList(), ct);
        for (var k = 0; k < routes.Count; k++)
        {
            if (geometries[k] != null)
                routes[k].Day.RouteGeoJson = geometries[k];
            else
                warnings.Add(new TripWarningDto { Code = TripWarningCodes.RouteMissing, Message = "Route not available", Day = routes[k].Day.Day });
        }

        dto.Warnings = warnings;
        logger.LogDebug("trip.plan {Mode} city={CityKey} leg={LegIndex} firstDayUsed={FirstDayUsed} spots={Spots} days={Days} osrm={Osrm} anchor={Anchor}",
            request.Mode, city.CityKey, request.LegIndex, firstDayUsed, allSpots.Count, dto.Days.Count, osrmAvailable, anchor != null);
        return dto;
    }

    // null when the city matrix is not ready (its build then runs in the background) or a chosen spot is not in it.
    private async Task<RoadMatrix?> CityMatrixSliceAsync(TripCityInfo city, List<TripSpot> spots, CancellationToken ct)
    {
        var matrix = await roadCache.PeekCityMatrixAsync(city.CityKey, ct);
        return matrix?.Slice(spots.Select(s => s.Id).ToList());
    }

    private static List<(double Lat, double Lon)> Points(IEnumerable<TripSpot> spots) => spots.Select(s => (s.Latitude, s.Longitude)).ToList();

    // The day route starts at last night's stay and ends at tonight's when they are known.
    private static List<(double Lat, double Lon)> RoutePoints(List<TripSpot> spots, TripSpot? start, TripSpot? end)
    {
        var points = Points(spots);
        if (points.Count == 0) return points;
        if (start != null) points.Insert(0, (start.Latitude, start.Longitude));
        if (end != null) points.Add((end.Latitude, end.Longitude));
        return points;
    }

    // Day points (stays) placed after the places in the matrix, one row per distinct point.
    private sealed class DayPoints(int offset)
    {
        public List<TripSpot> Spots { get; } = new();
        public int Count => Spots.Count;

        public int Add(TripSpot? p)
        {
            if (p == null) return -1;
            var i = Spots.FindIndex(x => x.Latitude == p.Latitude && x.Longitude == p.Longitude);
            if (i < 0) { Spots.Add(p); i = Spots.Count - 1; }
            return offset + i;
        }

        public TripSpot? At(int index) => index < offset || index - offset >= Spots.Count ? null : Spots[index - offset];
    }

    private static TripSpot ToSpot(PlanSpotInput s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Latitude = s.Latitude,
        Longitude = s.Longitude,
        Category = s.Category,
        VisitMinutes = s.VisitMinutes,
        ClosedDays = s.ClosedDays,
    };
}

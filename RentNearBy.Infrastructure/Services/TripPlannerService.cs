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

        var days = recalculate
            ? (request.Days ?? new List<PlanDayInput>())
                .Select(d => (d.Day, Spots: d.Spots.Select(ToSpot).ToList()))
                .ToList()
            : new List<(int Day, List<TripSpot> Spots)>();
        var allSpots = (recalculate
                ? days.SelectMany(d => d.Spots)
                : (request.Spots ?? new List<PlanSpotInput>()).Select(ToSpot))
            .DistinctBy(s => s.Id)
            .ToList();

        var centreLat = city.Latitude;
        var centreLon = city.Longitude;
        var anchor = TripAnchor.Spot(request.AnchorLatitude, request.AnchorLongitude);
        var matrixSpots = anchor == null ? allSpots : allSpots.Append(anchor).ToList();

        RoadMatrix? raw = null;
        if (matrixSpots.Count >= 2 && osrm.IsConfigured)
            raw = anchor == null
                ? await CityMatrixSliceAsync(city, allSpots, ct) ?? await osrm.TableAsync(Points(allSpots), ct)
                : await osrm.TableAsync(Points(matrixSpots), ct) ?? TripAnchor.Embed(await CityMatrixSliceAsync(city, allSpots, ct), matrixSpots.Count);
        var matrix = DaySplitter.FillEstimates(matrixSpots, raw, o.FallbackSpeedKmh, o.EstimateFactor);
        var osrmAvailable = raw != null;

        var warnings = new List<TripWarningDto>();
        if (!osrmAvailable && matrixSpots.Count >= 2)
            warnings.Add(new TripWarningDto { Code = TripWarningCodes.OsrmUnavailable, Message = "Road times are estimates" });

        var firstDayUsed = Math.Clamp(request.FirstDayUsedMinutes ?? 0, 0, 900);
        var result = recalculate
            ? DaySplitter.Recalculate(days, limit, request.StartDate, matrix, allSpots, firstDayUsed, anchor)
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
        foreach (var day in result.Days)
        {
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

            var points = RoutePoints(day.Spots, anchor);
            if (osrmAvailable && points.Count >= 2 && routes.Count < o.RouteGeometryMaxDays)
                routes.Add((dayDto, points));

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

    // With an anchor the day route starts and ends at it.
    private static List<(double Lat, double Lon)> RoutePoints(List<TripSpot> spots, TripSpot? anchor)
    {
        var points = Points(spots);
        if (anchor == null || points.Count == 0) return points;
        points.Insert(0, (anchor.Latitude, anchor.Longitude));
        points.Add((anchor.Latitude, anchor.Longitude));
        return points;
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

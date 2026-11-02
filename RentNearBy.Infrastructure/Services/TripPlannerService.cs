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
        var maxDays = Math.Clamp(o.MaxDays, 1, TripLimits.MaxDays);
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

        RoadMatrix? raw = null;
        if (allSpots.Count >= 2 && osrm.IsConfigured)
            raw = await CityMatrixSliceAsync(city, allSpots, ct)
                ?? await osrm.TableAsync(allSpots.Select(s => (s.Latitude, s.Longitude)).ToList(), ct);
        var matrix = DaySplitter.FillEstimates(allSpots, raw, o.FallbackSpeedKmh, o.EstimateFactor);
        var osrmAvailable = raw != null;

        var warnings = new List<TripWarningDto>();
        if (!osrmAvailable && allSpots.Count >= 2)
            warnings.Add(new TripWarningDto { Code = TripWarningCodes.OsrmUnavailable, Message = "Road times are estimates" });

        var result = recalculate
            ? DaySplitter.Recalculate(days, limit, request.StartDate, matrix, allSpots)
            : DaySplitter.Split(new SplitInput(allSpots, centreLat, centreLon, limit, request.StartDate, maxDays), matrix);

        warnings.AddRange(result.Warnings.Select(w => new TripWarningDto { Code = w.Code, Message = w.Message, SpotId = w.SpotId, Day = w.Day }));

        var dto = new TripPlanResultDto
        {
            CentreLatitude = centreLat,
            CentreLongitude = centreLon,
            OsrmAvailable = osrmAvailable,
            DayLimitMinutes = limit,
        };

        var routed = 0;
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

            if (osrmAvailable && day.Spots.Count >= 2 && routed < o.RouteGeometryMaxDays)
            {
                routed++;
                var geometry = await roadCache.GetRouteGeometryAsync(day.Spots.Select(s => (s.Latitude, s.Longitude)).ToList(), ct);
                if (geometry != null)
                    dayDto.RouteGeoJson = geometry;
                else
                    warnings.Add(new TripWarningDto { Code = TripWarningCodes.RouteMissing, Message = "Route not available", Day = day.Day });
            }

            dto.Days.Add(dayDto);
        }

        dto.Warnings = warnings;
        logger.LogDebug("trip.plan {Mode} city={CityKey} spots={Spots} days={Days} osrm={Osrm}",
            request.Mode, city.CityKey, allSpots.Count, dto.Days.Count, osrmAvailable);
        return dto;
    }

    // null when the city has no cached matrix or a chosen spot is not in it.
    private async Task<RoadMatrix?> CityMatrixSliceAsync(TripCityInfo city, List<TripSpot> spots, CancellationToken ct)
    {
        var matrix = await roadCache.GetCityMatrixAsync(city.CityKey, ct);
        return matrix?.Slice(spots.Select(s => s.Id).ToList());
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

using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface ITripPlannerService
{
    Task<TripPlanResultDto> PlanAsync(TripCityInfo city, PlanTripRequest request, CancellationToken ct = default);
}

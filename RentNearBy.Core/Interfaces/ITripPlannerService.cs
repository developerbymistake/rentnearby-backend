using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ITripPlannerService
{
    Task<TripPlanResultDto> PlanAsync(City city, PlanTripRequest request, CancellationToken ct = default);
}

using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// What an enquiry DTO needs of its trip: the dates for everyone, the plan only for the trip's owner (PlanJson null otherwise).
public sealed record TripEnquiryTripView(
    Guid Id, DateOnly? StartDate, DateOnly? EndDate, string? PlanJson,
    long CityKey, string CityName, double CityLatitude, double CityLongitude, Guid? DistrictId)
{
    // Same normalised plan as TripPlanReader.Read(trip); null without the owner's PlanJson.
    public TripPlan? Plan()
    {
        if (PlanJson == null) return null;
        return TripPlanReader.Read(new Trip
        {
            Id = Id,
            PlanJson = PlanJson,
            CityKey = CityKey,
            CityName = CityName,
            CityLatitude = CityLatitude,
            CityLongitude = CityLongitude,
            DistrictId = DistrictId,
        });
    }
}

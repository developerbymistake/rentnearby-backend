using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers.Owner.Be1;

namespace RentNearBy.Api.Endpoints.Owner;

// Contract 1.3. Mapped on the ungated /api/v1/taxis/me group (policy CanListTaxis) next to the calendar.
public static class TaxiDayOffEndpoints
{
    public static RouteGroupBuilder MapTaxiDayOffEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/days-off", TaxiDayOffHandlers.Read);
        group.MapPut("/days-off", TaxiDayOffHandlers.Save).AvailabilityHourLimited();
        return group;
    }
}

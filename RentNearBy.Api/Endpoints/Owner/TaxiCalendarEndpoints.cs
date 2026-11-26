using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers.Owner.Be1;

namespace RentNearBy.Api.Endpoints.Owner;

// Lane BE1, Taxi Driver (contract 2.1 C1-C11). Mapped on the ungated /api/v1/taxis/me group with policy CanListTaxis.
public static class TaxiCalendarEndpoints
{
    public static RouteGroupBuilder MapTaxiCalendarEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/calendar", TaxiCalendarHandlers.Month);
        group.MapGet("/calendar/upcoming", TaxiCalendarHandlers.Upcoming);
        group.MapGet("/calendar/past", TaxiCalendarHandlers.Past);
        group.MapGet("/calendar/entries/{id:guid}", TaxiCalendarHandlers.Entry);
        // Bucket B (SPEC 6): the four calendar writes; reads and the reminder switch are not in it.
        group.MapPost("/calendar/entries", TaxiCalendarHandlers.Create).BookingHourLimited();
        group.MapPut("/calendar/entries/{id:guid}", TaxiCalendarHandlers.Update).BookingHourLimited();
        group.MapDelete("/calendar/entries/{id:guid}", TaxiCalendarHandlers.Delete).BookingHourLimited();
        group.MapPut("/calendar/entries/{id:guid}/money", TaxiCalendarHandlers.Money).BookingHourLimited();
        group.MapGet("/calendar/summary", TaxiCalendarHandlers.Summary);
        group.MapGet("/calendar/balance-due", TaxiCalendarHandlers.BalanceDue);
        group.MapPut("/calendar/reminder", TaxiCalendarHandlers.Reminder);
        return group;
    }
}

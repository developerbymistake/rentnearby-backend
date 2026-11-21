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
        group.MapPost("/calendar/entries", TaxiCalendarHandlers.Create);
        group.MapPut("/calendar/entries/{id:guid}", TaxiCalendarHandlers.Update);
        group.MapDelete("/calendar/entries/{id:guid}", TaxiCalendarHandlers.Delete);
        group.MapPut("/calendar/entries/{id:guid}/money", TaxiCalendarHandlers.Money);
        group.MapGet("/calendar/summary", TaxiCalendarHandlers.Summary);
        group.MapGet("/calendar/balance-due", TaxiCalendarHandlers.BalanceDue);
        group.MapPut("/calendar/reminder", TaxiCalendarHandlers.Reminder);
        return group;
    }
}

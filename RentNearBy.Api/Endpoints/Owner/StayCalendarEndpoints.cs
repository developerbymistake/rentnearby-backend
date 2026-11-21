using RentNearBy.Api.Handlers.Owner.Be1;

namespace RentNearBy.Api.Endpoints.Owner;

// Lane BE1, Stay Owner (contract 2.1 C1-C11, 2.2 K1-K4). Mapped on the ungated /api/v1/stays/me group with policy CanListStays.
public static class StayCalendarEndpoints
{
    public static RouteGroupBuilder MapStayCalendarEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/calendar", StayCalendarHandlers.Month);
        group.MapGet("/calendar/upcoming", StayCalendarHandlers.Upcoming);
        group.MapGet("/calendar/past", StayCalendarHandlers.Past);
        group.MapGet("/calendar/entries/{id:guid}", StayCalendarHandlers.Entry);
        group.MapPost("/calendar/entries", StayCalendarHandlers.Create);
        group.MapPut("/calendar/entries/{id:guid}", StayCalendarHandlers.Update);
        group.MapDelete("/calendar/entries/{id:guid}", StayCalendarHandlers.Delete);
        group.MapPut("/calendar/entries/{id:guid}/money", StayCalendarHandlers.Money);
        group.MapGet("/calendar/summary", StayCalendarHandlers.Summary);
        group.MapGet("/calendar/balance-due", StayCalendarHandlers.BalanceDue);
        group.MapPut("/calendar/reminder", StayCalendarHandlers.Reminder);

        group.MapGet("/blocked-ranges", StayBlockedRangeHandlers.List);
        group.MapPost("/blocked-ranges", StayBlockedRangeHandlers.Create);
        group.MapPut("/blocked-ranges/{id:guid}", StayBlockedRangeHandlers.Update);
        group.MapDelete("/blocked-ranges/{id:guid}", StayBlockedRangeHandlers.Delete);
        return group;
    }
}

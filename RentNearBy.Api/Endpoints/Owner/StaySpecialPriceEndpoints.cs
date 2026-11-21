using RentNearBy.Api.Handlers.Owner.Be2;

namespace RentNearBy.Api.Endpoints.Owner;

// P1..P6 on the ungated Stay owner group /api/v1/stays/me (CanListStays).
public static class StaySpecialPriceEndpoints
{
    public static RouteGroupBuilder MapStaySpecialPriceEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/special-prices", StaySpecialPriceHandlers.List);
        group.MapPost("/special-prices", StaySpecialPriceHandlers.Create);
        group.MapPut("/special-prices/{id:guid}", StaySpecialPriceHandlers.Update);
        group.MapDelete("/special-prices/{id:guid}", StaySpecialPriceHandlers.Delete);
        group.MapPut("/special-prices/{id:guid}/pause", StaySpecialPriceHandlers.Pause);
        group.MapPut("/special-prices/{id:guid}/live", StaySpecialPriceHandlers.MakeLive);
        return group;
    }
}

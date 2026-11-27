using RentNearBy.Api.Filters;
using RentNearBy.Api.Handlers.Owner;

namespace RentNearBy.Api.Endpoints.Owner;

// Stay quotation (BUILD_PLAN 4.1) on the group /api/v1/stays/me/quotation (CanListStays, no tab gate). Filter order on every route:
// auth -> role -> own bucket -> owner write bucket -> handler (validation inside).
public static class StayQuotationEndpoints
{
    public static RouteGroupBuilder MapStayQuotationEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", StayQuotationHandlers.GetHub);
        group.MapGet("/profile/bank", StayQuotationHandlers.GetBank);
        group.MapPut("/profile", StayQuotationHandlers.PutProfile).OwnerWriteLimited();
        group.MapPut("/logo", StayQuotationHandlers.PutLogo).DisableAntiforgery().QuotePhotoLimited().PhotoBodyLimited();
        group.MapPut("/signature", StayQuotationHandlers.PutSignature).DisableAntiforgery().QuotePhotoLimited().PhotoBodyLimited();
        group.MapDelete("/logo", StayQuotationHandlers.DeleteLogo).OwnerWriteLimited();
        group.MapDelete("/signature", StayQuotationHandlers.DeleteSignature).OwnerWriteLimited();
        group.MapGet("/image/{kind}", StayQuotationHandlers.GetImage);
        group.MapPost("/terms/accept", StayQuotationHandlers.AcceptTerms).OwnerWriteLimited();
        group.MapPost("/reserve", StayQuotationHandlers.Reserve).QuoteReserveLimited();
        group.MapPost("/name-change", StayQuotationHandlers.RequestNameChange).QuoteNameLimited();
        return group;
    }
}

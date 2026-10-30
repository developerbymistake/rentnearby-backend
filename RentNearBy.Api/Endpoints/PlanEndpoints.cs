using RentNearBy.Api.Handlers;

namespace RentNearBy.Api.Endpoints;

// Not tab-gated: role and tab checks for purchases happen inside the plan service.
public static class PlanEndpoints
{
    public static RouteGroupBuilder MapPlanEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/catalog", PlanHandlers.GetCatalog).AllowAnonymous();
        group.MapGet("/me", PlanHandlers.GetMe).RequireAuthorization();
        group.MapPost("/quote", PlanHandlers.Quote).RequireAuthorization();
        group.MapPost("/purchases", PlanHandlers.CreatePurchase).RequireAuthorization();
        group.MapGet("/purchases", PlanHandlers.GetPurchases).RequireAuthorization();
        group.MapGet("/purchases/open", PlanHandlers.GetOpenPurchase).RequireAuthorization();
        group.MapGet("/purchases/{id:guid}", PlanHandlers.GetPurchase).RequireAuthorization();
        group.MapPost("/purchases/{id:guid}/verify", PlanHandlers.VerifyPurchase).RequireAuthorization();
        group.MapPost("/purchases/{id:guid}/verify-play", PlayBillingHandlers.VerifyPlayPurchase).RequireAuthorization();
        group.MapPost("/purchases/restore", PlayBillingHandlers.RestorePlayPurchases).RequireAuthorization();
        group.MapPost("/trial", PlanHandlers.StartTrial).RequireAuthorization();
        return group;
    }
}

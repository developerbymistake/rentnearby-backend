using RentNearBy.Api.Handlers.Buddy.Be3;

namespace RentNearBy.Api.Endpoints;

// Expenses E1 to E8 and E17 on the /api/v1/trips group (TripPlannerGateFilter on the group).
public static class TripExpenseEndpoints
{
    public static RouteGroupBuilder MapTripExpenseEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{tripId:guid}/expenses", TripExpenseHandlers.List).RequireAuthorization();
        group.MapGet("/{tripId:guid}/expenses/summary", TripExpenseHandlers.Summary).RequireAuthorization();
        group.MapPost("/{tripId:guid}/expenses/suggestions/skip", TripExpenseHandlers.SkipSuggestion).RequireAuthorization();
        group.MapPost("/{tripId:guid}/expenses/late-join/{userId:guid}/keep", TripExpenseHandlers.KeepLateJoiner).RequireAuthorization();
        group.MapPost("/{tripId:guid}/expenses/share-with", TripExpenseHandlers.ShareWith).RequireAuthorization();
        group.MapGet("/{tripId:guid}/expenses/{expenseId:guid}", TripExpenseHandlers.Detail).RequireAuthorization();
        group.MapPut("/{tripId:guid}/expenses/{expenseId:guid}", TripExpenseHandlers.Save).RequireAuthorization();
        group.MapDelete("/{tripId:guid}/expenses/{expenseId:guid}", TripExpenseHandlers.Delete).RequireAuthorization();
        group.MapGet("/{tripId:guid}/balances", TripExpenseHandlers.Balances).RequireAuthorization();
        return group;
    }
}

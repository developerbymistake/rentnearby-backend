using RentNearBy.Api.Extensions;
using RentNearBy.Api.Handlers.Buddy.Be3;

namespace RentNearBy.Api.Endpoints;

// Expenses E1 to E8 and E17 on the /api/v1/trips group (TripPlannerGateFilter on the group).
public static class TripExpenseEndpoints
{
    public static RouteGroupBuilder MapTripExpenseEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{tripId:guid}/expenses", TripExpenseHandlers.List).SeekerOnly();
        group.MapGet("/{tripId:guid}/expenses/summary", TripExpenseHandlers.Summary).SeekerOnly();
        group.MapPost("/{tripId:guid}/expenses/suggestions/skip", TripExpenseHandlers.SkipSuggestion).SeekerOnly();
        group.MapPost("/{tripId:guid}/expenses/late-join/{userId:guid}/keep", TripExpenseHandlers.KeepLateJoiner).SeekerOnly();
        group.MapPost("/{tripId:guid}/expenses/share-with", TripExpenseHandlers.ShareWith).SeekerOnly();
        group.MapGet("/{tripId:guid}/expenses/{expenseId:guid}", TripExpenseHandlers.Detail).SeekerOnly();
        group.MapPut("/{tripId:guid}/expenses/{expenseId:guid}", TripExpenseHandlers.Save).SeekerOnly();
        group.MapDelete("/{tripId:guid}/expenses/{expenseId:guid}", TripExpenseHandlers.Delete).SeekerOnly();
        group.MapGet("/{tripId:guid}/balances", TripExpenseHandlers.Balances).SeekerOnly();
        return group;
    }
}

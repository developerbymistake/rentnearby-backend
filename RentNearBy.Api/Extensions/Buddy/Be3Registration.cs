using Microsoft.Extensions.DependencyInjection.Extensions;
using RentNearBy.Api.Endpoints;
using RentNearBy.Api.Filters;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be3;

namespace RentNearBy.Api.Extensions.Buddy;

// Lane BE3 only. Expenses (E1-E8, E17); replaces ITripMoneyCards.
public static class Be3Registration
{
    public static IServiceCollection AddBe3Expenses(this IServiceCollection services)
    {
        services.AddScoped<TripExpenseService>();
        services.AddScoped<TripExpenseReader>();
        services.AddScoped<TripExpenseSuggestions>();
        services.AddScoped<TripBalanceReader>();
        services.Replace(ServiceDescriptor.Scoped<ITripMoneyCards, TripMoneyCards>());
        return services;
    }

    public static IEndpointRouteBuilder MapBe3Expenses(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/trips")
            .WithTags("Trips")
            .AddEndpointFilter<TripPlannerGateFilter>()
            .AddEndpointFilter<TripPlanPauseFilter>()
            .MapTripExpenseEndpoints();
        return app;
    }
}

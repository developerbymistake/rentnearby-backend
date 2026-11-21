using RentNearBy.Api.Endpoints.Owner;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be3;

namespace RentNearBy.Api.Extensions.Owner;

// Lane BE3 only. plans: plan keys 3.2, hidden rows, M1-M3, V1-V2, live stay edit and A3, ring weight, Featured, T6 (IPlanReconcileSignal singleton).
// Services: register here (a plain Add* after the foundation's TryAdd default wins). Routes: map here; new owner groups carry no TabGateFilter.
public static class OwnerBe3Registration
{
    public static IServiceCollection AddOwnerBe3(this IServiceCollection services)
    {
        services.AddSingleton<IPlanReconcileSignal, PlanReconcileSignal>();
        services.AddScoped<PlanLimitsReconcile>();
        services.AddScoped<PlanVisibleService>();
        services.AddScoped<StayHubReader>();
        services.AddScoped<TaxiHubReader>();
        services.AddScoped<StayPendingEditService>();
        return services;
    }

    public static IEndpointRouteBuilder MapOwnerBe3(this IEndpointRouteBuilder app)
    {
        app.MapPlanVisibleEndpoints();
        app.MapAdminStayEditEndpoints();
        return app;
    }
}

namespace RentNearBy.Api.Extensions.Owner;

// Lane BE3 only. plans: plan keys 3.2, hidden rows, M1-M3, V1-V2, live stay edit and A3, ring weight, Featured, T6 (IPlanReconcileSignal singleton).
// Services: register here (a plain Add* after the foundation's TryAdd default wins). Routes: map here; new owner groups carry no TabGateFilter.
public static class OwnerBe3Registration
{
    public static IServiceCollection AddOwnerBe3(this IServiceCollection services)
    {
        return services;
    }

    public static IEndpointRouteBuilder MapOwnerBe3(this IEndpointRouteBuilder app)
    {
        return app;
    }
}

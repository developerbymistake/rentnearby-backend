namespace RentNearBy.Api.Extensions.Owner;

// Lane BE2 only. pricing: P1-P6, F1, A2, T1-T5, T7 (IStayDatedPricing, IStayDetailPricing), festival cache.
// Services: register here (a plain Add* after the foundation's TryAdd default wins). Routes: map here; new owner groups carry no TabGateFilter.
public static class OwnerBe2Registration
{
    public static IServiceCollection AddOwnerBe2(this IServiceCollection services)
    {
        return services;
    }

    public static IEndpointRouteBuilder MapOwnerBe2(this IEndpointRouteBuilder app)
    {
        return app;
    }
}

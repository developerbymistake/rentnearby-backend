namespace RentNearBy.Api.Extensions.Owner;

// Lane BE1 only. calendar: C1-C11, K1-K4, money notes, Auto hooks (IOwnerCalendarSync singleton), repair job, retention rules, listing purge (IOwnerListingPurge).
// Services: register here (a plain Add* after the foundation's TryAdd default wins). Routes: map here; new owner groups carry no TabGateFilter.
public static class OwnerBe1Registration
{
    public static IServiceCollection AddOwnerBe1(this IServiceCollection services)
    {
        return services;
    }

    public static IEndpointRouteBuilder MapOwnerBe1(this IEndpointRouteBuilder app)
    {
        return app;
    }
}

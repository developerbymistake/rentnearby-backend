namespace RentNearBy.Api.Extensions.Owner;

// Lane BE4 only. feeds and lifecycle: H1, H2, notification jobs and kinds (IOwnerNotifier), account deletion and logout server parts, admin plan API A1, A4-A7.
// Services: register here (a plain Add* after the foundation's TryAdd default wins). Routes: map here; new owner groups carry no TabGateFilter.
public static class OwnerBe4Registration
{
    public static IServiceCollection AddOwnerBe4(this IServiceCollection services)
    {
        return services;
    }

    public static IEndpointRouteBuilder MapOwnerBe4(this IEndpointRouteBuilder app)
    {
        return app;
    }
}

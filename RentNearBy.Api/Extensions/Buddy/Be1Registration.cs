namespace RentNearBy.Api.Extensions.Buddy;

// Lane BE1 only. Buddies, block, report (B1-B17, admin buddy-hold).
public static class Be1Registration
{
    public static IServiceCollection AddBe1Buddies(this IServiceCollection services)
    {
        return services;
    }

    public static IEndpointRouteBuilder MapBe1Buddies(this IEndpointRouteBuilder app)
    {
        return app;
    }
}

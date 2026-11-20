namespace RentNearBy.Api.Extensions.Buddy;

// Lane BE2 only. Trip sharing (S1-S17); replace ITripSharingHooks and ITripAccess with services.Replace.
public static class Be2Registration
{
    public static IServiceCollection AddBe2Sharing(this IServiceCollection services)
    {
        return services;
    }

    public static IEndpointRouteBuilder MapBe2Sharing(this IEndpointRouteBuilder app)
    {
        return app;
    }
}

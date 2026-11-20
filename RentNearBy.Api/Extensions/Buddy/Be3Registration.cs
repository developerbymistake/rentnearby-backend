namespace RentNearBy.Api.Extensions.Buddy;

// Lane BE3 only. Expenses (E1-E8, E17); replace ITripMoneyCards with services.Replace.
public static class Be3Registration
{
    public static IServiceCollection AddBe3Expenses(this IServiceCollection services)
    {
        return services;
    }

    public static IEndpointRouteBuilder MapBe3Expenses(this IEndpointRouteBuilder app)
    {
        return app;
    }
}

namespace RentNearBy.Api.Extensions.Buddy;

// Lane BE4 only. Payments and notices (E9, E10, E12, E18, E19, N1, N2); replace INoticeWriter and ITripMoneyGuard with services.Replace.
public static class Be4Registration
{
    public static IServiceCollection AddBe4PaymentsAndNotices(this IServiceCollection services)
    {
        return services;
    }

    public static IEndpointRouteBuilder MapBe4PaymentsAndNotices(this IEndpointRouteBuilder app)
    {
        return app;
    }
}

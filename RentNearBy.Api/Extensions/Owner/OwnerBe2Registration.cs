using RentNearBy.Api.Endpoints.Owner;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be2;

namespace RentNearBy.Api.Extensions.Owner;

// Lane BE2 only. pricing: P1-P6, F1, A2, T1-T5, T7 (IStayDatedPricing, IStayDetailPricing), festival cache.
// Services: register here (a plain Add* after the foundation's TryAdd default wins). Routes: map here; new owner groups carry no TabGateFilter.
public static class OwnerBe2Registration
{
    public static IServiceCollection AddOwnerBe2(this IServiceCollection services)
    {
        services.AddSingleton<FestivalCache>();
        services.AddScoped<FestivalService>();
        services.AddScoped<StaySpecialPriceService>();
        services.AddScoped<IStayDatedPricing, StayDatedPricing>();
        services.AddScoped<IStayDetailPricing, StayDetailPricing>();
        return services;
    }

    public static IEndpointRouteBuilder MapOwnerBe2(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/stays/me").WithTags("Stay owner").RequireAuthorization("CanListStays").MapStaySpecialPriceEndpoints();
        app.MapGroup("/api/v1/festivals").WithTags("Festivals").MapFestivalEndpoints();
        app.MapGroup("/api/v1/admin").WithTags("Festivals").MapAdminFestivalEndpoints();
        return app;
    }
}

using RentNearBy.Api.Endpoints.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Quote;

namespace RentNearBy.Api.Extensions.Owner;

// Stay quotation (lane BE): master, images, terms, reserve, name change, the all-roles subscription deletion guard.
public static class StayQuotationRegistration
{
    public static IServiceCollection AddStayQuotation(this IServiceCollection services)
    {
        services.AddScoped<IQuoteImageStore, PhotoService>();
        services.AddScoped<StayQuoteProfileService>();
        services.AddScoped<StayQuoteReserveService>();
        services.AddScoped<StayQuoteImageService>();
        services.AddScoped<StayQuoteRoleCleanup>();
        services.AddScoped<SubscriptionDeletionContext>();
        services.AddScoped<SubscriptionDeletionPreCheck>();
        services.AddScoped<RentNearBy.Core.Interfaces.Buddy.IAccountDeletionParticipant, SubscriptionDeletionGuard>();
        return services;
    }

    public static IEndpointRouteBuilder MapStayQuotation(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/stays/me/quotation").WithTags("Stay quotation").RequireAuthorization("CanListStays").MapStayQuotationEndpoints();
        return app;
    }
}

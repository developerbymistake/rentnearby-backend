using Microsoft.Extensions.DependencyInjection.Extensions;
using RentNearBy.Api.Endpoints;
using RentNearBy.Api.Filters;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be4;

namespace RentNearBy.Api.Extensions.Buddy;

// Lane BE4 only. Payments and notices (E9, E10, E12, E18, E19, N1, N2); replaces INoticeWriter and ITripMoneyGuard.
public static class Be4Registration
{
    public static IServiceCollection AddBe4PaymentsAndNotices(this IServiceCollection services)
    {
        services.AddScoped<NoticeWriter>();
        services.Replace(ServiceDescriptor.Scoped<INoticeWriter>(sp => sp.GetRequiredService<NoticeWriter>()));
        services.Replace(ServiceDescriptor.Scoped<ITripMoneyGuard, TripMoneyGuard>());
        services.AddScoped<NoticeReader>();
        services.AddScoped<TripPaymentService>();
        services.AddScoped<IBuddyFeatureJob, SettleReminderJob>();
        services.AddScoped<IAccountDeletionParticipant, MoneyAccountDeletion>();
        return services;
    }

    public static IEndpointRouteBuilder MapBe4PaymentsAndNotices(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/trips")
            .WithTags("Trips")
            .AddEndpointFilter<TripPlannerGateFilter>()
            .MapTripPaymentEndpoints();

        app.MapGroup("/api/v1/notifications")
            .WithTags("Notifications")
            .MapNoticeEndpoints();

        return app;
    }
}

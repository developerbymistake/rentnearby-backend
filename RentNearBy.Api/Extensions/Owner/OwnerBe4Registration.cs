using Microsoft.Extensions.DependencyInjection.Extensions;
using RentNearBy.Api.Endpoints.Owner;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be4;

namespace RentNearBy.Api.Extensions.Owner;

// Lane BE4 only. feeds and lifecycle: H1, H2, notification jobs and kinds (IOwnerNotifier), account deletion and logout server parts, admin plan API A1, A4-A7.
// Services: register here (a plain Add* after the foundation's TryAdd default wins). Routes: map here; new owner groups carry no TabGateFilter.
public static class OwnerBe4Registration
{
    public static IServiceCollection AddOwnerBe4(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Scoped<IOwnerNotifier, OwnerNotifier>());
        services.AddScoped<StayOwnerHomeReader>();
        services.AddScoped<TaxiDriverHomeReader>();
        services.AddScoped<SeekerFeedReader>();
        services.AddScoped<AdminOwnerReads>();
        services.AddSingleton<OwnerShownCounter>();
        services.AddScoped<IBuddyFeatureJob, StayMorningReminderJob>();
        services.AddScoped<IBuddyFeatureJob, TaxiMorningReminderJob>();
        services.AddScoped<IBuddyFeatureJob, FestivalReminderJob>();
        services.AddScoped<IBuddyFeatureJob, OwnerWeeklyStatsFlushJob>();
        services.AddScoped<IBuddyFeatureJob, OwnerPlansAnnounceJob>();
        services.AddScoped<IAccountDeletionParticipant, OwnerAccountDeletion>();
        return services;
    }

    public static IEndpointRouteBuilder MapOwnerBe4(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/owner")
            .WithTags("Owner home")
            .RequireAuthorization()
            .MapOwnerHomeEndpoints();

        app.MapGroup("/api/v1/admin")
            .WithTags("Admin owner plans")
            .RequireAuthorization("AdminOnly")
            .MapAdminOwnerPlanEndpoints();

        return app;
    }
}

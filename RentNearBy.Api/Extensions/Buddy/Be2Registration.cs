using Microsoft.Extensions.DependencyInjection.Extensions;
using RentNearBy.Api.Endpoints;
using RentNearBy.Api.Filters;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Infrastructure.Services.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be2;

namespace RentNearBy.Api.Extensions.Buddy;

// Lane BE2 only. Trip sharing (S1-S17); replaces ITripSharingHooks, keeps ITripAccess (TripAccess, owned by BE2).
public static class Be2Registration
{
    public static IServiceCollection AddBe2Sharing(this IServiceCollection services)
    {
        services.AddScoped<TripInviteService>();
        services.AddScoped<TripMemberService>();
        services.AddScoped<TripViewerReader>();
        services.AddScoped<TripSharedListReader>();
        services.Replace(ServiceDescriptor.Scoped<ITripAccess, TripAccess>());
        services.Replace(ServiceDescriptor.Scoped<ITripSharingHooks, TripSharingHooks>());
        services.AddScoped<IUserBlockParticipant, TripInviteBlockParticipant>();
        services.AddScoped<IAccountDeletionParticipant, TripSharingAccountDeletion>();
        services.AddScoped<IBuddyFeatureJob, TripInviteExpiryJob>();
        return services;
    }

    public static IEndpointRouteBuilder MapBe2Sharing(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/trips")
            .WithTags("Trips")
            .AddEndpointFilter<TripPlannerGateFilter>()
            .AddEndpointFilter<TripPlanPauseFilter>()
            .MapTripSharingEndpoints();
        return app;
    }
}

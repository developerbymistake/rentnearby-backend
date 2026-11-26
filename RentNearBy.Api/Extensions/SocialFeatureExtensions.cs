using Microsoft.AspNetCore.SignalR;
using RentNearBy.Api.Endpoints;
using RentNearBy.Api.Hubs;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Infrastructure.Services.Social;

namespace RentNearBy.Api.Extensions;

// Bakhli social (CONTRACT_SOCIAL): services, jobs, participants, hub filter and the /api/v1/social routes.
public static class SocialFeatureExtensions
{
    public static IServiceCollection AddSocialFeature(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ISeekerLimits, SeekerLimitsService>();
        services.AddScoped<SocialAfterCommit>();
        services.AddScoped<SeekerPlanReconcile>();
        services.AddScoped<PublicTripReader>();
        services.AddScoped<PublicTripService>();
        services.AddScoped<SocialProfileService>();
        services.AddScoped<INameChangeGate, NameChangeGate>();
        services.AddScoped<FollowService>();
        services.AddScoped<MyBuddyService>();
        services.AddScoped<JoinRequestService>();
        services.AddScoped<SocialChatService>();
        services.AddScoped<ISocialChatService>(sp => sp.GetRequiredService<SocialChatService>());
        services.AddScoped<ITripGroupChat, TripGroupChat>();
        services.AddScoped<SocialReportService>();
        services.AddScoped<IUserBlockParticipant, SocialBlockParticipant>();
        services.AddScoped<IAccountDeletionParticipant, SocialDeletionParticipant>();
        services.AddScoped<IBuddyFeatureJob, ChatExpiryPurgeJob>();
        services.AddScoped<IBuddyFeatureJob, JoinRequestExpiryJob>();

        services.AddSingleton<HubConnectionRegistry>();
        services.AddSingleton<ISocialRealtime, SocialHubEvents>();
        services.AddSingleton<IHubSessionRevoker, HubSessionRevoker>();
        services.AddSingleton<SocialHubFilter>();
        services.AddSignalR(o => o.AddFilter<SocialHubFilter>());
        return services;
    }

    public static IEndpointRouteBuilder MapSocialFeature(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/social").WithTags("Social").MapSocialEndpoints();
        app.MapGroup("/api/v1/admin").WithTags("SocialAdmin").MapSocialAdminEndpoints();
        return app;
    }
}

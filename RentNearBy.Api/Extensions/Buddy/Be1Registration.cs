using Microsoft.Extensions.Caching.Memory;
using RentNearBy.Api.Endpoints;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be1;
using RentNearBy.Infrastructure.Services.Social;
using StackExchange.Redis;

namespace RentNearBy.Api.Extensions.Buddy;

// Lane BE1 only. Buddies, block, report (B1-B17, admin buddy-hold).
public static class Be1Registration
{
    public static IServiceCollection AddBe1Buddies(this IServiceCollection services)
    {
        services.AddScoped<BuddyFindService>();
        services.AddSingleton<IBuddyFoundMarks>(sp => sp.GetService<IConnectionMultiplexer>() is { } redis
            ? new RedisBuddyFoundMarks(redis)
            : new MemoryBuddyFoundMarks(sp.GetRequiredService<IMemoryCache>()));
        services.AddScoped<BuddyRequestService>();
        services.AddScoped<BuddyListService>();
        services.AddScoped<UserReportService>();
        services.AddScoped<IUserBlockService, UserBlockService>();
        services.AddScoped<IBuddyFeatureJob, BuddyRequestExpiryJob>();
        services.AddScoped<IAccountDeletionParticipant, BuddyAccountDeletion>();
        // Discover travellers (Buddy_resume/discover/DESIGN.md 3): Redis snapshot cache (no Redis = no cache) and the service.
        services.AddSingleton(sp => new DiscoverCache(sp.GetService<IConnectionMultiplexer>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<DiscoverCache>>()));
        services.AddScoped<IDiscoverService, DiscoverService>();
        return services;
    }

    public static IEndpointRouteBuilder MapBe1Buddies(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/buddies").WithTags("Buddies").MapBuddyEndpoints();
        app.MapGroup("/api/v1/users").WithTags("Buddies").MapPersonSafetyEndpoints();
        app.MapGroup("/api/v1/report-reasons").WithTags("Buddies").MapUserReportReasonEndpoints();
        app.MapGroup("/api/v1/admin").WithTags("Buddies").MapAdminBuddyEndpoints();
        return app;
    }
}

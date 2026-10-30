using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;

namespace RentNearBy.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Maximum Pool Size is set directly in the DATABASE_URL env var on the server (see
        // SERVER_PERFORMANCE_TUNING.md, Point 4), not hardcoded here — a deployment-level tuning
        // knob belongs in deployment config, adjustable without a code change/rebuild.
        var connectionString = configuration["DATABASE_URL"]
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DATABASE_URL not configured");

        // Pooled instead of AddDbContext: reuses ApplicationDbContext instances across requests
        // instead of allocating/disposing one per request. Safe here — audited the whole
        // Backend for the two pooling pitfalls: ApplicationDbContext itself has no custom
        // fields to leak state between requests, and every consumer (all handlers, all 10
        // background workers via IServiceScopeFactory.CreateScope(), AccountDeletionService,
        // the plan services) resolves it as Scoped, never captured by a Singleton.
        services.AddDbContextPool<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString, o => o.UseNetTopologySuite()));

        services.AddMemoryCache();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IPhotoService, PhotoService>();
        services.AddScoped<IListingPurgeService, ListingPurgeService>();
        services.AddScoped<IRoleChangeGuardService, RoleChangeGuardService>();
        services.AddScoped<IAccountDeletionService, AccountDeletionService>();
        services.AddScoped<ICityResolutionService, CityResolutionService>();
        services.AddScoped<IPlanCatalogCache, PlanCatalogCache>();
        services.AddScoped<IPaymentFeatureReader, PaymentFeatureReader>();
        services.AddScoped<IPlanEntitlementService, PlanEntitlementService>();
        services.AddScoped<ILiveSlotService, PlanSlotService>();
        services.AddScoped<IPlanPurchaseService, PlanPurchaseService>();
        services.AddScoped<IListingCacheInvalidator, ListingCacheInvalidator>();

        // Google Play Billing. Secrets come from PLAY_SERVICE_ACCOUNT_JSON / PLAY_ACCOUNT_ID_SALT when the
        // PlayBilling section leaves them empty; missing config only turns the Play endpoints into 503s.
        services.Configure<PlayBillingOptions>(configuration.GetSection(PlayBillingOptions.Section));
        services.PostConfigure<PlayBillingOptions>(o =>
        {
            if (string.IsNullOrWhiteSpace(o.ServiceAccountJson)) o.ServiceAccountJson = configuration["PLAY_SERVICE_ACCOUNT_JSON"];
            if (string.IsNullOrWhiteSpace(o.AccountIdSalt)) o.AccountIdSalt = configuration["PLAY_ACCOUNT_ID_SALT"] ?? string.Empty;
            o.RoleChangeAction = NormalizeRoleChangeAction(o.RoleChangeAction) ?? PlayServerActions.Cancel;
        });
        services.AddSingleton<IPlayBillingClient, PlayBillingClient>();
        services.AddSingleton<IPlayRtdnAuthenticator, PlayRtdnAuthenticator>();
        services.AddScoped<IPlaySubscriptionRepository, PlaySubscriptionRepository>();
        services.AddScoped<IPlayEntitlementApplier, PlayEntitlementApplier>();
        services.AddScoped<IPlayBillingSyncService, PlayBillingSyncService>();

        // Hourly: plan expiry claims, 7-day reminders, daily rank reconcile.
        services.AddHostedService<PlanLifecycleService>();
        // Every 2 min: settles paid orders, refunds blocked grants, expires stale open orders, and runs the
        // Google Play passes (server actions, due/stale syncs, acknowledgements, daily voided purchases).
        services.AddHostedService<PlanPurchaseReconcilerService>();

        // Hourly (anchored at :15) sweep of rooms/plots past their own ValidUntil.
        services.AddHostedService<ListingExpirySweepService>();
        // Register background service for district digest aggregation (runs at 4:00 AM IST daily)
        services.AddHostedService<DistrictDigestJobService>();
        // Purges DeletedAccountRecords past their 180-day retention window (runs at 5:00 AM IST daily)
        services.AddHostedService<DeletedAccountRecordPurgeService>();

        services.AddHttpClient<IRazorpayService, RazorpayService>();

        services.AddHttpClient<IGeocodingService, NominatimGeocodingService>(client =>
        {
            client.BaseAddress = new Uri("https://nominatim.developerbymistake.tech/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Bakhli/1.0 (support@bakhli.in)");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddHttpClient<IOverpassService, OverpassService>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Bakhli/1.0 (support@bakhli.in)");
            client.Timeout = TimeSpan.FromSeconds(70); // query timeout=60 + buffer
        });

        var redisUrl = configuration["REDIS_URL"];
        if (!string.IsNullOrWhiteSpace(redisUrl))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var uri = new Uri(redisUrl);
                var options = new ConfigurationOptions
                {
                    AbortOnConnectFail = false,
                    ConnectTimeout = 3000,
                    AsyncTimeout = 1000
                };
                options.EndPoints.Add(uri.Host, uri.Port);
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var parts = uri.UserInfo.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        options.User = Uri.UnescapeDataString(parts[0]);
                        options.Password = Uri.UnescapeDataString(parts[1]);
                    }
                }
                return ConnectionMultiplexer.Connect(options);
            });
            services.AddSingleton<IRateLimitService, RedisRateLimitService>();
            services.AddSingleton<IOtpStore, RedisOtpStore>();
        }
        else
        {
            services.AddSingleton<IRateLimitService, InMemoryRateLimitService>();
            services.AddSingleton<IOtpStore, MemoryOtpStore>();
        }

        services.AddHttpClient<IOtpService, WhatsAppOtpService>();

        // FCM — Singleton: FirebaseApp.Create() must be called only once
        services.AddSingleton<IFcmService, FcmService>();

        // RabbitMQ publisher — Singleton: IConnection is long-lived and expensive
        services.AddSingleton<IRabbitMqPublisher, RabbitMqPublisher>();

        // Notification worker — consumes listing.expired queue and sends FCM
        services.AddHostedService<NotificationWorkerService>();

        // DLQ worker — consumes dlq.listing.expired (failed/retried notifications)
        services.AddHostedService<DlqNotificationWorkerService>();

        // Broadcast worker — consumes broadcast.notification queue and sends FCM to all users
        services.AddHostedService<BroadcastWorkerService>();

        // District digest worker — consumes district.digest.ready queue and sends FCM topic push
        services.AddHostedService<DistrictDigestWorkerService>();

        // Report-filed worker — consumes report.filed queue and notifies the listing owner
        services.AddHostedService<ReportFiledWorkerService>();

        // Chat FCM — independent of IFcmService/FcmService above (see IChatFcmService),
        // Singleton for the same FirebaseApp.Create()-once reason.
        services.AddSingleton<IChatFcmService, ChatFcmService>();

        // Chat push worker — consumes chat.message.push queue and sends FCM via ChatFcmService
        services.AddHostedService<ChatMessageNotificationWorkerService>();

        // Notification-inbox push worker — consumes notification.push queue, delivers FCM for any
        // NotificationEvent row from any producer — fully
        // separate from every other push worker, no shared queue or code.
        services.AddHostedService<NotificationPushWorkerService>();

        // Go-Live-requested worker — consumes golive.requested queue (a fresh, never-before-approved
        // Go-Live submission) and notifies admins via AdminDeviceTokens + FCM (same pattern as
        // ReportFiledWorkerService) — deliberately not the NotificationEvent/inbox system.
        services.AddHostedService<GoLiveRequestedWorkerService>();

        // Role-requested worker: consumes role.requested and notifies admins via AdminDeviceTokens + FCM.
        services.AddHostedService<RoleRequestedWorkerService>();

        // Stay-job-posted seeker fan-out: consumes stayjob.posted.seekers (no DLQ — queue args must stay null to match RabbitMqPublisher), FCM to every active Seeker's valid tokens in batches.
        services.AddHostedService<StayJobPostedSeekerWorkerService>();

        // Stay-job-posted admin nudge: consumes stayjob.posted.admin, FCM via AdminDeviceTokens (own queue, not golive.requested).
        services.AddHostedService<StayJobPostedAdminWorkerService>();

        return services;
    }

    // Contract D-13: role change / account deletion queue Cancel or RevokeProrated only; anything else is a config error.
    public static string? NormalizeRoleChangeAction(string? value)
    {
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, PlayServerActions.Cancel, StringComparison.OrdinalIgnoreCase)) return PlayServerActions.Cancel;
        if (string.Equals(trimmed, PlayServerActions.RevokeProrated, StringComparison.OrdinalIgnoreCase)) return PlayServerActions.RevokeProrated;
        return null;
    }
}

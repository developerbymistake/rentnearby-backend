using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Services;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Retention;
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

        // Taxi driving licence encryption. Key from IDENTITY_ENCRYPTION_KEY; missing key fails closed (503 on a licence write).
        services.AddSingleton<IIdentityCipher, AesGcmIdentityCipher>();

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

        // Trip Planner: Atlas (places), OSRM (road times), config gate. Missing Atlas/OSRM config never crashes.
        services.Configure<TripPlannerOptions>(configuration.GetSection(TripPlannerOptions.Section));
        services.PostConfigure<TripPlannerOptions>(o =>
        {
            if (bool.TryParse(configuration["TRIP_PLANNER_ENABLED"], out var enabled)) o.Enabled = enabled;
            o.CityCatalogPageSize = Math.Clamp(o.CityCatalogPageSize, 1, 200);
            o.CityCatalogMaxPages = Math.Clamp(o.CityCatalogMaxPages, 1, 500);
            o.CityCatalogRevalidateSeconds = Math.Clamp(o.CityCatalogRevalidateSeconds, 30, 3600);
            o.CitiesPageSize = Math.Clamp(o.CitiesPageSize, 1, 200);
        });
        services.Configure<AtlasOptions>(configuration.GetSection(AtlasOptions.Section));
        services.PostConfigure<AtlasOptions>(o =>
        {
            if (string.IsNullOrWhiteSpace(o.BaseUrl)) o.BaseUrl = configuration["ATLAS_BASE_URL"];
            if (string.IsNullOrWhiteSpace(o.ApiKey)) o.ApiKey = configuration["ATLAS_API_KEY"];
        });
        services.Configure<OsrmOptions>(configuration.GetSection(OsrmOptions.Section));
        services.PostConfigure<OsrmOptions>(o =>
        {
            if (string.IsNullOrWhiteSpace(o.BaseUrl)) o.BaseUrl = configuration["OSRM_URL"];
            if (int.TryParse(configuration["OSRM_MAX_TABLE_COORDINATES"], out var maxTable) && maxTable > 0) o.MaxTableCoordinates = maxTable;
        });
        services.AddHttpClient<IAtlasClient, AtlasClient>().RedactLoggedHeaders(new[] { "X-Atlas-Api-Key" });
        services.AddHttpClient<IOsrmClient, OsrmClient>();
        services.AddSingleton<ITripSpotCache, TripSpotCache>();
        services.AddSingleton<ITripCityCatalog, TripCityCatalog>();
        services.AddSingleton<CityResolveCache>();
        services.AddSingleton<IAreaGeocoder>(sp => (IAreaGeocoder)sp.GetRequiredService<IGeocodingService>());
        services.AddSingleton<ParentCityResolver>();
        services.AddSingleton<ICityResolver, CityResolver>();
        services.AddSingleton<INearbyContext, NearbyContext>();
        services.AddScoped<INearbyFeedQuery, NearbyFeedQuery>();
        services.AddSingleton<ITripRoadCache, TripRoadCache>();
        services.AddScoped<ITripPlannerService, TripPlannerService>();
        // Consumes the atlas.city.changed fanout on queue atlas.city.changed.rentnearby.
        services.AddHostedService<AtlasCityChangedWorkerService>();

        // Trip bookings (enquiries, offers). TRIP_EXECUTION_ENABLED=false pauses the routes and the timer.
        services.Configure<TripExecutionOptions>(configuration.GetSection(TripExecutionOptions.Section));
        services.PostConfigure<TripExecutionOptions>(o =>
        {
            if (bool.TryParse(configuration["TRIP_EXECUTION_ENABLED"], out var enabled)) o.Enabled = enabled;
        });
        services.AddScoped<IConversationFactory, ConversationFactory>();
        services.AddScoped<ITripChatService, TripChatService>();
        services.AddScoped<ITripNotifier, TripNotifier>();
        services.AddScoped<ITripEnquiryClosure, TripEnquiryClosure>();
        // Every 5 min: offer and enquiry expiry, urgency refresh, purges.
        services.AddHostedService<TripTimerService>();
        services.AddTaxiWays(configuration);

        // Hourly: plan expiry claims, 7-day reminders, daily rank reconcile.
        services.AddHostedService<PlanLifecycleService>();
        // Every 2 min: settles paid orders, refunds blocked grants, expires stale open orders, and runs the
        // Google Play passes (server actions, due/stale syncs, acknowledgements, daily voided purchases).
        services.AddHostedService<PlanPurchaseReconcilerService>();

        // Purges DeletedAccountRecords past their 180-day retention window (runs at 5:00 AM IST daily)
        services.AddHostedService<DeletedAccountRecordPurgeService>();

        // Nightly retention (01:00-04:30 IST, one rule at a time); rules are found by assembly scan.
        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.Section));
        services.PostConfigure<RetentionOptions>(o => o.Normalize());
        services.AddRetentionRules(typeof(RetentionRunner).Assembly);
        services.AddHostedService<RetentionRunner>();

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

        // Trip push pipeline: transactional outbox -> confirmed publisher -> rnb.push/push.send -> batched FCM, plus the 3-day morning reminder run.
        // Additive: the old notification.push queue and its worker are untouched.
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddSingleton<OutboxSignal>();
        services.AddSingleton<IOutboxSignal>(sp => sp.GetRequiredService<OutboxSignal>());
        services.AddSingleton<IConfirmedPublisher, ConfirmedRabbitPublisher>();
        services.AddSingleton<IPushDeliveryStore, PushDeliveryStore>();
        services.AddScoped<DeviceTokenRepository>();
        services.AddHostedService<OutboxRelayService>();
        services.AddHostedService<PushDeliveryWorkerService>();
        services.AddHostedService<TripReminderService>();

        // Broadcast worker — consumes broadcast.notification queue and sends FCM to all users
        services.AddHostedService<BroadcastWorkerService>();

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

        // Plan purchase admin push: consumes plan.purchase.admin (granted Play charge, chargeback review), FCM via AdminDeviceTokens.
        services.AddHostedService<PlanPurchaseAdminWorkerService>();

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

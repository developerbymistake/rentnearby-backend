using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;
using StackExchange.Redis;

namespace RentNearBy.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["DATABASE_URL"]
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DATABASE_URL not configured");

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString, o => o.UseNetTopologySuite()));

        services.AddMemoryCache();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IPhotoService, PhotoService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IAccountDeletionService, AccountDeletionService>();

        // Register background service for membership expiry (runs at 12:00 AM daily)
        services.AddHostedService<MembershipExpiryService>();
        // Register background service for plot membership expiry (runs at 1:00 AM daily)
        services.AddHostedService<PlotMembershipExpiryService>();
        // Register background service for district digest aggregation (runs at 4:00 AM IST daily)
        services.AddHostedService<DistrictDigestJobService>();

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

        // Notification worker — consumes membership.expired queue and sends FCM
        services.AddHostedService<NotificationWorkerService>();

        // DLQ worker — consumes dlq.membership.expired (failed/retried notifications)
        services.AddHostedService<DlqNotificationWorkerService>();

        // Broadcast worker — consumes broadcast.notification queue and sends FCM to all users
        services.AddHostedService<BroadcastWorkerService>();

        // District digest worker — consumes district.digest.ready queue and sends FCM topic push
        services.AddHostedService<DistrictDigestWorkerService>();

        return services;
    }
}

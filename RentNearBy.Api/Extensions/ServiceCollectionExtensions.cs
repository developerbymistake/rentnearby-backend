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
            options.UseNpgsql(connectionString));

        services.AddMemoryCache();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IPhotoService, PhotoService>();

        services.AddHttpClient<IGeocodingService, NominatimGeocodingService>(client =>
        {
            client.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RentNearBy/1.0 (admin@rentnearby.in)");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        var redisUrl = configuration["REDIS_URL"];
        if (!string.IsNullOrWhiteSpace(redisUrl))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var uri = new Uri(redisUrl);
                var options = new ConfigurationOptions { AbortOnConnectFail = false };
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
        }
        else
        {
            services.AddSingleton<IRateLimitService, InMemoryRateLimitService>();
        }

        return services;
    }
}

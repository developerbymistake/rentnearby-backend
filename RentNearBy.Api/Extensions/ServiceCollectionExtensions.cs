using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services;

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

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IPhotoService, PhotoService>();

        return services;
    }

    public static WebApplication UsePhotoStorage(this WebApplication app)
    {
        var uploadPath = app.Configuration["Storage:UploadPath"]
            ?? throw new InvalidOperationException("Storage:UploadPath not configured");

        Directory.CreateDirectory(uploadPath);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(uploadPath),
            RequestPath = "/uploads"
        });

        return app;
    }
}

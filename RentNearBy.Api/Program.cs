using FluentValidation;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Endpoints;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Mappings;
using RentNearBy.Api.Middleware;
using RentNearBy.Api.Validators;

var builder = WebApplication.CreateBuilder(args);

DtoMappings.ConfigureMappings();

builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o =>
    o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o =>
    o.Level = System.IO.Compression.CompressionLevel.Fastest);

builder.Services.AddValidatorsFromAssemblyContaining<SendOtpRequestValidator>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.WithOrigins(
            "https://developerbymistake.tech"
        )
        .AllowAnyHeader()
        .AllowAnyMethod());
});


builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "RentNearBy API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "JWT Authorization header. Example: \"Bearer {token}\"",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RentNearBy.Infrastructure.Data.ApplicationDbContext>();
    // Drop all non-extension tables (skips PostGIS/extension-owned tables like spatial_ref_sys)
    await db.Database.ExecuteSqlRawAsync("""
        DO $$ DECLARE r RECORD;
        BEGIN
            FOR r IN
                SELECT c.relname AS tablename
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public'
                AND c.relkind = 'r'
                AND NOT EXISTS (
                    SELECT 1 FROM pg_depend d
                    JOIN pg_extension e ON d.refobjid = e.oid
                    WHERE d.objid = c.oid
                    AND d.deptype = 'e'
                )
            LOOP
                EXECUTE 'DROP TABLE IF EXISTS public."' || r.tablename || '" CASCADE';
            END LOOP;
        END $$;
    """);
    await db.Database.EnsureCreatedAsync();
    await RentNearBy.Infrastructure.Data.DataSeeder.SeedAsync(db);
}

app.UseResponseCompression();
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", async (IServiceProvider sp) =>
{
    var multiplexer = sp.GetService<StackExchange.Redis.IConnectionMultiplexer>();
    string redisStatus;
    if (multiplexer == null)
    {
        redisStatus = "not configured";
    }
    else
    {
        try
        {
            await multiplexer.GetDatabase().PingAsync();
            redisStatus = "connected";
        }
        catch
        {
            redisStatus = "unavailable";
        }
    }

    var photoService = sp.GetRequiredService<RentNearBy.Infrastructure.Services.IPhotoService>();
    var cloudinaryOk = await photoService.PingAsync();

    return Results.Ok(new
    {
        status = "healthy",
        timestamp = DateTime.UtcNow,
        redis = redisStatus,
        cloudinary = cloudinaryOk ? "connected" : "unavailable",
    });
});

app.MapGroup("/api/v1/auth")
    .WithTags("Authentication")
    .MapAuthEndpoints();

app.MapGroup("/api/v1/users")
    .WithTags("Users")
    .MapUsersEndpoints();

app.MapGroup("/api/v1/listings")
    .WithTags("Listings")
    .MapListingsEndpoints();

app.MapGroup("/api/v1/admin")
    .WithTags("Admin")
    .MapAdminEndpoints();

app.MapGroup("/api/v1/plots")
    .WithTags("Plots")
    .MapPlotEndpoints();

app.Run();

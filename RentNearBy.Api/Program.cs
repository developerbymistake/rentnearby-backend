using FluentValidation;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Endpoints;
using RentNearBy.Api.Hubs;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Mappings;
using RentNearBy.Api.Middleware;
using RentNearBy.Api.Validators;

var builder = WebApplication.CreateBuilder(args);

DtoMappings.ConfigureMappings();

builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddSignalR();

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
    try
    {
        if (app.Environment.IsDevelopment())
        {
            // Local dev only: wipe and rebuild from the current model on every restart,
            // for fast iteration. Never runs outside Development.
            Console.WriteLine("[STARTUP] Dropping all tables...");
            await db.Database.ExecuteSqlRawAsync("""
                DO $$ DECLARE r RECORD;
                BEGIN
                    FOR r IN (
                        SELECT tablename FROM pg_tables
                        WHERE schemaname = 'public'
                        AND tablename NOT IN ('spatial_ref_sys', 'geometry_columns', 'geography_columns', 'raster_columns', 'raster_overviews')
                    )
                    LOOP
                        EXECUTE 'DROP TABLE IF EXISTS public.' || quote_ident(r.tablename) || ' CASCADE';
                    END LOOP;
                END $$;
            """);
            Console.WriteLine("[STARTUP] Tables dropped. Running EnsureCreated...");
            await db.Database.EnsureCreatedAsync();
            Console.WriteLine("[STARTUP] Schema created. Running seeder...");
        }
        else
        {
            // Production/staging: never destroy existing data.
            //
            // Self-baselining, one-time-in-practice: if this DB's schema was already
            // built outside the migration system (e.g. by a prior EnsureCreatedAsync
            // run, so its tables already exist) but has no migration history yet, mark
            // the current migration as already applied — without re-running it — so
            // MigrateAsync() below only ever applies genuinely NEW migrations from here
            // on. This block is a no-op on every subsequent restart once the history
            // table exists — same idempotent, leave-it-in-forever pattern as the seeder.
            var historyExists = await db.Database
                .SqlQueryRaw<bool>("SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = '__EFMigrationsHistory')")
                .SingleAsync();
            var schemaAlreadyExists = await db.Database
                .SqlQueryRaw<bool>("SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'Users')")
                .SingleAsync();

            if (!historyExists && schemaAlreadyExists)
            {
                Console.WriteLine("[STARTUP] Pre-existing schema with no migration history detected — baselining...");
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                        "MigrationId" character varying(150) NOT NULL,
                        "ProductVersion" character varying(32) NOT NULL,
                        CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                    );
                    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260706171753_InitialCreate', '9.0.4')
                    ON CONFLICT ("MigrationId") DO NOTHING;
                """);
                Console.WriteLine("[STARTUP] Baseline recorded.");
            }

            Console.WriteLine("[STARTUP] Applying pending migrations...");
            await db.Database.MigrateAsync();
            Console.WriteLine("[STARTUP] Migrations applied. Running seeder...");
        }

        await RentNearBy.Infrastructure.Data.DataSeeder.SeedAsync(db);
        Console.WriteLine("[STARTUP] Seeder complete.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[STARTUP ERROR] {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        throw;
    }
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

app.MapGet("/delete-account", () => Results.Content("""
<!DOCTYPE html>
<html lang="en">
<head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Delete Account – Bakhli</title>
<style>body{font-family:sans-serif;max-width:600px;margin:48px auto;padding:0 24px;color:#1e293b}h1{color:#1e3a8a}a{color:#2563eb}.card{background:#f1f5f9;border-radius:12px;padding:24px;margin:24px 0}.step{display:flex;align-items:flex-start;margin:12px 0}.num{background:#1e3a8a;color:white;border-radius:50%;width:28px;height:28px;display:flex;align-items:center;justify-content:center;font-weight:bold;margin-right:12px;flex-shrink:0}.warn{background:#fef2f2;border-left:4px solid #ef4444;padding:16px;border-radius:8px;margin:24px 0}</style>
</head>
<body>
<h1>Delete Your Bakhli Account</h1>
<p>You can delete your account directly from the Bakhli app. The deletion is <strong>immediate and permanent</strong>.</p>
<div class="card">
<strong>Steps to delete your account in the app:</strong><br><br>
<div class="step"><div class="num">1</div><span>Open the Bakhli app and sign in</span></div>
<div class="step"><div class="num">2</div><span>Go to <strong>Profile</strong> (bottom navigation)</span></div>
<div class="step"><div class="num">3</div><span>Scroll down and tap <strong>"Delete Account"</strong></span></div>
<div class="step"><div class="num">4</div><span>Type <strong>DELETE</strong> to confirm</span></div>
<div class="step"><div class="num">5</div><span>Tap <strong>Confirm</strong> — your account is deleted instantly</span></div>
</div>
<div class="warn">
<strong>Warning:</strong> This action is permanent and cannot be undone. All your listings, plots, photos, bookings, and membership data will be permanently removed.
</div>
<p>If you are unable to access the app, contact us at <a href="mailto:supportbakhli@gmail.com">supportbakhli@gmail.com</a> with subject <em>"Account Deletion Request"</em>.</p>
<p><a href="https://developerbymistake.github.io/bakhli-privacy-policy/">Privacy Policy</a></p>
</body></html>
""", "text/html"));

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

app.MapGroup("/api/v1/admin-auth")
    .WithTags("AdminAuth")
    .MapAdminAuthEndpoints();

app.MapGroup("/api/v1/users")
    .WithTags("Users")
    .MapUsersEndpoints();

app.MapGroup("/api/v1/listings")
    .WithTags("RoomListings")
    .MapListingsEndpoints();

app.MapGroup("/api/v1/admin")
    .WithTags("Admin")
    .MapAdminEndpoints();

app.MapGroup("/api/v1/plots")
    .WithTags("PlotListings")
    .MapPlotListingEndpoints();

app.MapGroup("/api/v1/admin/plots")
    .WithTags("AdminPlotListings")
    .MapAdminPlotListingEndpoints();

app.MapGroup("/api/v1/account")
    .WithTags("Account")
    .MapAccountEndpoints();

app.MapGroup("/api/v1/notifications")
    .WithTags("Notifications")
    .MapNotificationEndpoints();

app.MapGroup("/api/v1/payments")
    .WithTags("Payments")
    .MapPaymentEndpoints();

app.MapGroup("/api/v1")
    .WithTags("Banners")
    .MapBannerEndpoints();

app.MapGroup("/api/v1/admin")
    .WithTags("AdminBanners")
    .MapAdminBannerEndpoints();

app.MapHub<BannerHub>("/hubs/banner");

app.Run();

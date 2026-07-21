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
        // Both branches build schema via Migrate() only — never EnsureCreatedAsync().
        // EnsureCreatedAsync() creates tables straight from the model but never writes
        // to __EFMigrationsHistory, so the next MigrateAsync() call (Dev or Production)
        // finds history and reality disagree and fails. Using Migrate() everywhere
        // means history always matches whichever environment last touched the schema.
        if (app.Environment.IsDevelopment())
        {
            // Local dev only: wipe everything and rebuild from scratch via Migrate(),
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
            Console.WriteLine("[STARTUP] Tables dropped. Applying migrations...");
            await db.Database.MigrateAsync();
            Console.WriteLine("[STARTUP] Schema created via migrations. Running seeder...");
        }
        else
        {
            // Production/staging: never destroy existing data. Applies only pending
            // migrations (additive).
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

app.MapGet("/.well-known/assetlinks.json", (IConfiguration configuration) =>
{
    var packageName = configuration["AppLinks:PackageName"];
    var fingerprints = configuration.GetSection("AppLinks:Sha256Fingerprints").Get<string[]>() ?? [];
    return Results.Json(new object[]
    {
        new
        {
            relation = new[] { "delegate_permission/common.handle_all_urls" },
            target = new
            {
                @namespace = "android_app",
                package_name = packageName,
                sha256_cert_fingerprints = fingerprints,
            },
        },
    });
});

// Smart marketing link for QR codes/posters: the OS intercepts this URL before it ever reaches this
// handler when Android has verified the App Link and the app is installed (see assetlinks.json above) —
// this route only ever executes for "app not installed", "verification not yet propagated", or a
// non-Android visitor. Because App Links already handled the "app is installed" case at the OS level
// before this page could ever load, there's no custom-scheme/timer gambit needed here — this page can
// go straight to the platform store the instant it loads, based on a client-side UA check (server-side
// UA sniffing is unreliable in WebViews/in-app browsers, hence JS not a redirect header).
app.MapGet("/app", (IConfiguration configuration) =>
{
    var playStoreUrl = configuration["AppLinks:PlayStoreUrl"] ?? "";
    var appStoreUrl = configuration["AppLinks:AppStoreUrl"] ?? "";
    var appStoreButton = string.IsNullOrEmpty(appStoreUrl)
        ? ""
        : $"""<a class="btn store" href="{appStoreUrl}">Download on the App Store</a>""";

    var html = """
<!DOCTYPE html>
<html lang="en">
<head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Bakhli — Rooms & Plots Near You</title>
<meta name="description" content="Find nearby rooms, PG, flats & plots for rent. Browse on a live map and connect straight with owners.">
<meta property="og:title" content="Bakhli — Rooms & Plots Near You">
<meta property="og:description" content="Find nearby rooms, PG, flats & plots for rent. Browse on a live map and connect straight with owners.">
<meta property="og:url" content="https://developerbymistake.tech/app">
<meta property="og:type" content="website">
<meta name="twitter:card" content="summary">
<style>body{font-family:sans-serif;max-width:520px;margin:64px auto;padding:0 24px;color:#1e293b;text-align:center}h1{color:#1e3a8a;margin-bottom:8px}p.tagline{color:#475569;margin-top:0}.card{background:#f1f5f9;border-radius:12px;padding:32px 24px;margin:24px 0}.btn{display:block;background:#1e3a8a;color:white;text-decoration:none;padding:14px 20px;border-radius:8px;font-weight:bold;margin:12px auto;max-width:280px}.btn.store{background:#000}.hint{font-size:13px;color:#64748b;margin-top:16px}</style>
</head>
<body>
<h1>Bakhli</h1>
<p class="tagline">Rooms, PG, flats &amp; plots for rent — near you.</p>
<div class="card">
<p>Taking you to the app…</p>
<a class="btn" href="__PLAY_STORE_URL__">Get it on Google Play</a>
__APP_STORE_BUTTON__
<p class="hint">If nothing happens automatically, tap the button above.</p>
</div>
<script>
(function () {
  var ua = navigator.userAgent || navigator.vendor || "";
  var playStoreUrl = __PLAY_STORE_URL_JS__;
  var appStoreUrl = __APP_STORE_URL_JS__;
  if (/android/i.test(ua) && playStoreUrl) {
    window.location.replace(playStoreUrl);
  } else if (/iPad|iPhone|iPod/.test(ua) && appStoreUrl) {
    window.location.replace(appStoreUrl);
  }
})();
</script>
</body></html>
"""
        .Replace("__PLAY_STORE_URL__", playStoreUrl)
        .Replace("__APP_STORE_BUTTON__", appStoreButton)
        .Replace("__PLAY_STORE_URL_JS__", System.Text.Json.JsonSerializer.Serialize(playStoreUrl))
        .Replace("__APP_STORE_URL_JS__", System.Text.Json.JsonSerializer.Serialize(appStoreUrl));

    return Results.Content(html, "text/html");
});

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
<strong>Warning:</strong> This action is permanent and cannot be undone. All your listings, plots, photos, bookings, and wallet coin balance will be permanently removed.
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

app.MapGroup("/api/v1/services")
    .WithTags("ServiceCatalog")
    .MapServiceCatalogEndpoints();

app.MapGroup("/api/v1/admin")
    .WithTags("AdminServiceCatalog")
    .MapAdminServiceCatalogEndpoints();

app.MapGroup("/api/v1/agents")
    .WithTags("Agents")
    .MapAgentEndpoints();

app.MapGroup("/api/v1/inquiries")
    .WithTags("Inquiries")
    .MapInquiryEndpoints();

app.MapGroup("/api/v1/admin/inquiries")
    .WithTags("AdminInquiries")
    .MapAdminInquiryEndpoints();

app.MapGroup("/api/v1/admin/notifications")
    .WithTags("AdminNotifications")
    .MapAdminNotificationEndpoints();

app.MapGroup("/api/v1/chat")
    .WithTags("Chat")
    .MapChatEndpoints();

app.MapGroup("/api/v1/home")
    .WithTags("Home")
    .MapHomeEndpoints();

app.MapGroup("/api/v1/config")
    .WithTags("Config")
    .MapConfigEndpoints();

app.MapGroup("/api/v1/coupons")
    .WithTags("Coupons")
    .MapCouponEndpoints();

app.MapGroup("/api/v1/coin-packs")
    .WithTags("CoinPacks")
    .MapCoinPackEndpoints();

app.MapGroup("/api/v1/wallet")
    .WithTags("Wallet")
    .MapWalletEndpoints();

app.MapHub<BannerHub>("/hubs/banner");
app.MapHub<ChatHub>("/hubs/chat");
app.MapHub<WalletHub>("/hubs/wallet");
app.MapHub<InquiryHub>("/hubs/inquiry");

app.Run();

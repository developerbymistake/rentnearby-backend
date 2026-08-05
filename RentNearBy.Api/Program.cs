using FluentValidation;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.HttpOverrides;
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

// Deployed behind Coolify's Traefik reverse proxy — without this, HttpContext.Connection.RemoteIpAddress
// is always Traefik's own container IP, not the real client IP, which silently collapses every per-IP
// rate limit in WebEnquiryHandlers into one shared bucket for all visitors instead of throttling any one
// abuser (see that file's ClientIp() comment). KnownNetworks/KnownProxies are cleared deliberately —
// Coolify's proxy IP isn't a fixed, known-in-advance address — which means the *immediate* hop's
// X-Forwarded-For entry is trusted unconditionally. That is only safe as long as this container is never
// reachable directly from the public internet (only through Traefik) — docker-compose.yml currently
// publishes 5000:5000 on the host, so confirm the host firewall/security group blocks external access to
// that port directly, or a caller could forge X-Forwarded-For and defeat these rate limits entirely.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

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
            "https://developerbymistake.tech",
            // Bakhli marketing website (bakhli-website repo) — added for the public web-enquiry flow
            // (WebEnquiryEndpoints) and the read-only services/packages catalog it calls from the browser.
            "https://bakhli.com",
            "https://www.bakhli.com"
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

// Non-fatal by design (see RazorpayService's _webhookSecret field comment) — the client-driven
// verify-call and the 30-min Razorpay reconciliation sweep both work without it, so this must not
// block startup. Still needs to be loud: also surfaced at GET /health as razorpayWebhook.
using (var scope = app.Services.CreateScope())
{
    var razorpayService = scope.ServiceProvider.GetRequiredService<RentNearBy.Infrastructure.Services.IRazorpayService>();
    if (!razorpayService.IsWebhookConfigured)
    {
        var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        startupLogger.LogError(
            "Razorpay:WebhookSecret / RAZORPAY_WEBHOOK_SECRET is not configured — every incoming Razorpay webhook will be rejected. " +
            "Credit purchases still complete via the client verify-call and the 30-min reconciliation sweep, but instant crash-recovery credit is degraded.");
    }
}

app.UseForwardedHeaders();
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
    var html = RenderStoreRedirectHtml(playStoreUrl, appStoreUrl, "https://developerbymistake.tech/app");
    return Results.Content(html, "text/html");
});

// Sibling to /app above, kept as its own path (per the user's explicit call to keep future QR
// use-cases on their own paths under the same already-verified host) rather than a modification of
// /app — {type}/{slug} identify which listing the QR code was printed for (Room "r" vs Plot "p"),
// but this route deliberately does NOT fetch listing data or set listing-specific OG tags: like /app,
// it exists only to catch "app not installed"/"verification not yet propagated" and hand off to the
// Play/App Store, config-driven via the same AppLinks:PlayStoreUrl. The Android App Link + the app's
// own deep-link resolver (GET /listings|plots/by-slug/{slug}) are what actually resolve {type}/{slug}
// to a listing when the app IS installed — this HTML page is the "not installed" fallback only.
// Route constraints below match the actual value shapes ("r"/"p" for type, SlugGenerator's
// lowercase-alphanumeric-hyphenated output for slug — see SlugGenerator.cs) so a request carrying
// anything else (e.g. an HTML/script-breakout payload) 404s before the handler ever runs, rather than
// being accepted and relying solely on output encoding below.
app.MapGet("/go/{type:regex(^(r|p)$)}/{slug:regex(^[a-z0-9]+(-[a-z0-9]+)*$)}", (string type, string slug, IConfiguration configuration) =>
{
    var playStoreUrl = configuration["AppLinks:PlayStoreUrl"] ?? "";
    var appStoreUrl = configuration["AppLinks:AppStoreUrl"] ?? "";
    // Deferred deep link (Google Play Install Referrer): carry type+slug through the Play Store
    // install as one opaque `referrer` value, so the app's first-ever launch after a fresh
    // install can land on this same listing (see Frontend's DeepLinkService._consumeInstallReferrer)
    // instead of losing this context the way a plain store redirect would.
    var referrerPayload = $"type={Uri.EscapeDataString(type)}&slug={Uri.EscapeDataString(slug)}";
    var playStoreUrlWithReferrer = string.IsNullOrEmpty(playStoreUrl)
        ? playStoreUrl
        : $"{playStoreUrl}&referrer={Uri.EscapeDataString(referrerPayload)}";
    // ogUrl is still HTML-encoded inside RenderStoreRedirectHtml before being placed into the og:url
    // attribute — defense in depth, not reliant on the route constraints above being exhaustive.
    var html = RenderStoreRedirectHtml(playStoreUrlWithReferrer, appStoreUrl, $"https://developerbymistake.tech/go/{type}/{slug}");
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
<strong>Warning:</strong> This action is permanent and cannot be undone. All your listings, plots, photos, bookings, and wallet credit balance will be permanently removed.
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

    // Deliberately not fatal at startup if missing (see the field comment on RazorpayService's
    // _webhookSecret) — but a missing/misrotated webhook secret silently disables the fastest credit-
    // add path (the client-driven verify-call and the 30-min Razorpay reconciliation sweep both
    // still work), so it needs to be loudly visible somewhere. Here, not a thrown exception.
    var razorpayService = sp.GetRequiredService<RentNearBy.Infrastructure.Services.IRazorpayService>();

    return Results.Ok(new
    {
        status = "healthy",
        timestamp = DateTime.UtcNow,
        redis = redisStatus,
        cloudinary = cloudinaryOk ? "connected" : "unavailable",
        razorpayWebhook = razorpayService.IsWebhookConfigured ? "configured" : "not configured",
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

app.MapGroup("/api/v1/enquiries")
    .WithTags("Enquiries")
    .MapEnquiryEndpoints();

// Public (unauthenticated) website enquiry flow — see WebEnquiryHandlers' doc comment. Entirely separate
// route group from the above; touches no existing enquiry route/handler.
app.MapGroup("/api/v1/web-enquiry")
    .WithTags("WebEnquiry")
    .MapWebEnquiryEndpoints();

app.MapGroup("/api/v1/admin/enquiries")
    .WithTags("AdminEnquiries")
    .MapAdminEnquiryEndpoints();

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

app.MapGroup("/api/v1/credit-packs")
    .WithTags("CreditPacks")
    .MapCreditPackEndpoints();

app.MapGroup("/api/v1/wallet")
    .WithTags("Wallet")
    .MapWalletEndpoints();

app.MapHub<BannerHub>("/hubs/banner");
app.MapHub<ChatHub>("/hubs/chat");
app.MapHub<WalletHub>("/hubs/wallet");
app.MapHub<EnquiryHub>("/hubs/enquiry");

app.Run();

// Shared UA-sniffed Play/App-Store redirect page, used by both /app (generic QR/poster link) and
// /go/{type}/{slug} (per-listing QR link) above, so the two routes never duplicate this HTML — only
// the og:url differs between callers. See the client-side-UA-check comment on the /app route for why
// this is JS-driven rather than a server-side redirect.
static string RenderStoreRedirectHtml(string playStoreUrl, string appStoreUrl, string ogUrl)
{
    var appStoreButton = string.IsNullOrEmpty(appStoreUrl)
        ? ""
        : $"""<a class="btn store" href="{appStoreUrl}">Download on the App Store</a>""";

    return """
<!DOCTYPE html>
<html lang="en">
<head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Bakhli — Rooms & Plots Near You</title>
<meta name="description" content="Find nearby rooms, PG, flats & plots for rent. Browse on a live map and connect straight with owners.">
<meta property="og:title" content="Bakhli — Rooms & Plots Near You">
<meta property="og:description" content="Find nearby rooms, PG, flats & plots for rent. Browse on a live map and connect straight with owners.">
<meta property="og:url" content="__OG_URL__">
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
        .Replace("__OG_URL__", HtmlEncoder.Default.Encode(ogUrl))
        .Replace("__PLAY_STORE_URL__", playStoreUrl)
        .Replace("__APP_STORE_BUTTON__", appStoreButton)
        .Replace("__PLAY_STORE_URL_JS__", System.Text.Json.JsonSerializer.Serialize(playStoreUrl))
        .Replace("__APP_STORE_URL_JS__", System.Text.Json.JsonSerializer.Serialize(appStoreUrl));
}

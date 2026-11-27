using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Extensions.Owner;
using RentNearBy.Api.Validators;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.Owner.Quote;
using RentNearBy.Tests.Owner.Be2;
using RentNearBy.Tests.SeekerOnly;

namespace RentNearBy.Tests.Stays.Quotation;

// Q21 / BUILD_PLAN 7.1 over real HTTP (Kestrel on loopback): the REAL quotation group (MapStayQuotation, the real handlers, filters
// and services) walked route by route. No token = 401, a Seeker / Taxi Driver / admin token = empty 403, all before any filter,
// bucket or handler runs. A Stay Owner passes the gate and reaches the first business step (here a refusing rate limiter = 429),
// which proves the gate is the role and nothing else. The principal comes from a header scheme because the live role read of
// OnTokenValidated needs PostgreSQL; the database is never opened (design-only context). One owner never reading another
// owner's data is proven on PostgreSQL by QuoteProfileTests and QuoteImageSwapTests (every statement is keyed by the token user).
public sealed class QuoteAuthTests : IAsyncLifetime
{
    private const string RoleHeader = "X-Test-Role";
    private WebApplication? _app;
    private HttpClient? _client;

    // method, path, body: none | json | form
    private static readonly (string Method, string Path, string Body)[] RouteList =
    [
        ("GET", "/api/v1/stays/me/quotation", "none"),
        ("GET", "/api/v1/stays/me/quotation/profile/bank", "none"),
        ("PUT", "/api/v1/stays/me/quotation/profile", "json"),
        ("PUT", "/api/v1/stays/me/quotation/logo", "form"),
        ("PUT", "/api/v1/stays/me/quotation/signature", "form"),
        ("DELETE", "/api/v1/stays/me/quotation/logo", "none"),
        ("DELETE", "/api/v1/stays/me/quotation/signature", "none"),
        ("GET", "/api/v1/stays/me/quotation/image/logo", "none"),
        ("POST", "/api/v1/stays/me/quotation/terms/accept", "json"),
        ("POST", "/api/v1/stays/me/quotation/reserve", "json"),
        ("POST", "/api/v1/stays/me/quotation/name-change", "json"),
    ];

    public static TheoryData<string, string, string> Routes
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var (method, path, body) in RouteList) data.Add(method, path, body);
            return data;
        }
    }

    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role)) return Task.FromResult(AuthenticateResult.NoResult());
            var value = role.ToString();
            var principal = value == "admin" ? SeekerRouteTable.AdminUser() : SeekerRouteTable.User(value);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    // Every bucket refuses: a caller who passed the gate gets 429 from the first step of the route.
    private sealed class RefusingLimiter : IRateLimitService
    {
        public int Checks;

        public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1)
        {
            Interlocked.Increment(ref Checks);
            return Task.FromResult(new RateLimitResult(false, 0, TimeSpan.FromSeconds(30)));
        }

        public Task ReleaseAsync(string key, string token) => Task.CompletedTask;
    }

    private readonly RefusingLimiter _limiter = new();

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        // Only the services the walked routes resolve are real; the rest of AddStayQuotation is never built here.
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateOnBuild = false);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization(AuthenticationExtensions.ConfigurePolicies);
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, SeekerForbiddenResultHandler>();
        builder.Services.AddValidatorsFromAssemblyContaining<QuoteProfileRequestValidator>();
        builder.Services.AddScoped(_ => new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only", o => o.UseNetTopologySuite()).Options));
        builder.Services.AddSingleton<IIdentityCipher>(QuoteKit.Cipher());
        builder.Services.AddSingleton<IPlanEntitlementService>(new QuoteEntitlements());
        builder.Services.AddSingleton<IOwnerNotifier>(new RecordingOwnerNotifier());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IRateLimitService>(_limiter);
        builder.Services.AddStayQuotation();
        builder.Services.AddScoped<IQuoteImageStore, RecordingQuoteImageStore>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapStayQuotation();
        await app.StartAsync();

        _app = app;
        _client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(string method, string path, string body, string? role)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (role != null) request.Headers.Add(RoleHeader, role);
        request.Content = body switch
        {
            "json" => new StringContent("{}", Encoding.UTF8, "application/json"),
            "form" => new MultipartFormDataContent { { new StringContent("x"), "note" } },
            _ => null,
        };
        using var response = await _client!.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_walk_covers_every_quotation_route()
    {
        var mapped = SeekerRouteTable.Routes.Keys.Where(k => k.Contains("/api/v1/stays/me/quotation", StringComparison.Ordinal))
            .Select(k => k.Replace("{kind}", "logo", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal);
        var walked = RouteList.Select(r => $"{r.Method} {r.Path}").OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(mapped, walked);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task No_token_is_401(string method, string path, string body)
    {
        var before = _limiter.Checks;
        var (status, text) = await SendAsync(method, path, body, null);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Empty(text);
        Assert.Equal(before, _limiter.Checks);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Seeker_taxi_driver_and_admin_tokens_are_403(string method, string path, string body)
    {
        foreach (var role in new[] { UserRoles.Seeker, UserRoles.TaxiDriver, "admin" })
        {
            var before = _limiter.Checks;
            var (status, text) = await SendAsync(method, path, body, role);
            Assert.True(status == HttpStatusCode.Forbidden, $"{role} {method} {path} answered {(int)status}");
            Assert.Empty(text);
            Assert.Equal(before, _limiter.Checks);
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_stay_owner_passes_the_gate_and_reaches_the_first_step(string method, string path, string body)
    {
        var before = _limiter.Checks;
        var (status, _) = await SendAsync(method, path, body, UserRoles.StayOwner);
        Assert.True(status == HttpStatusCode.TooManyRequests, $"{method} {path} answered {(int)status}");
        Assert.True(_limiter.Checks > before);
    }
}

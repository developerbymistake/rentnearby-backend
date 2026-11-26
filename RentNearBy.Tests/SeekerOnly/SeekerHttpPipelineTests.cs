using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Filters;

namespace RentNearBy.Tests.SeekerOnly;

// R7 over real HTTP (Kestrel on loopback, no database): the policies, SeekerGate.SeekerOnly(), the result handler and the
// trip planner gate run in the framework's own middleware order. The route table test proves every real route carries its
// policy; the principal here comes from a header scheme because the live role read of OnTokenValidated needs PostgreSQL.
public sealed class SeekerHttpPipelineTests : IAsyncLifetime
{
    private const string RoleHeader = "X-Test-Role";
    private const string NoRole = "none";
    private WebApplication? _app;
    private HttpClient? _client;

    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role)) return Task.FromResult(AuthenticateResult.NoResult());
            var value = role.ToString();
            var principal = value == "admin" ? SeekerRouteTable.AdminUser() : SeekerRouteTable.User(value == NoRole ? null : value);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization(AuthenticationExtensions.ConfigurePolicies);
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, SeekerForbiddenResultHandler>();
        builder.Services.Configure<TripPlannerOptions>(o => o.Enabled = false);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGroup("/api/v1/trips").AddEndpointFilter<TripPlannerGateFilter>()
            .MapGet("/", () => Results.Ok("trips")).SeekerOnly();
        var open = app.MapGroup("/api/v1/open");
        open.MapGet("/buddies", () => Results.Ok("buddies")).SeekerOnly();
        open.MapGet("/leads/driver", () => Results.Ok("leads")).RequireAuthorization("CanListTaxis");
        open.MapGet("/notifications", () => Results.Ok("inbox")).RequireAuthorization();
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

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path, string? role)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (role != null) request.Headers.Add(RoleHeader, role);
        using var response = await _client!.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static (string? Type, string? Message) ErrorOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("error");
        return (error.GetProperty("type").GetString(), error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(UserRoles.StayOwner)]
    [InlineData(UserRoles.TaxiDriver)]
    [InlineData("admin")]
    [InlineData(NoRole)]
    public async Task A_non_seeker_gets_the_typed_403_before_any_endpoint_filter(string role)
    {
        foreach (var path in new[] { "/api/v1/trips/", "/api/v1/open/buddies" })
        {
            var (status, body) = await GetAsync(path, role);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal((AuthPolicies.ErrorType, AuthPolicies.SeekerOnlyMessage), ErrorOf(body));
        }
    }

    [Fact]
    public async Task A_seeker_passes_the_gate()
    {
        var (status, body) = await GetAsync("/api/v1/open/buddies", UserRoles.Seeker);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("\"buddies\"", body);

        // Past the policy the next refusal is the trip planner switch, not the role.
        var (tripStatus, tripBody) = await GetAsync("/api/v1/trips/", UserRoles.Seeker);
        Assert.Equal(HttpStatusCode.Forbidden, tripStatus);
        Assert.Equal(TripErrorTypes.TripPlannerDisabled, ErrorOf(tripBody).Type);
    }

    [Fact]
    public async Task Anonymous_gets_401_not_the_seeker_body()
    {
        var (status, body) = await GetAsync("/api/v1/open/buddies", null);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Empty(body);
    }

    [Fact]
    public async Task Owner_routes_stay_open_for_their_role_and_keep_the_empty_403()
    {
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/open/leads/driver", UserRoles.TaxiDriver)).Status);
        var (status, body) = await GetAsync("/api/v1/open/leads/driver", UserRoles.Seeker);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(body);

        foreach (var role in new[] { UserRoles.Seeker, UserRoles.StayOwner, UserRoles.TaxiDriver })
            Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/open/notifications", role)).Status);
    }
}

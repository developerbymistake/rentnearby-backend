using System.Reflection;
using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Endpoints;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Validators;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Tests.TaxiRequests;

// Contract sections 5, 6, 7 and 11 as mapped by ApiRouteMap (app.MapTaxiRequests); T1-T10 are seeker-only (seeker-only contract 1.3, 1.4): each route once, its auth, its policy and
// its gates. The services are the real registrations, so a handler parameter the container cannot supply fails the build.
public class TaxiRequestEndpointMapTests
{
    private enum Auth { User, Seeker, Taxi, Anonymous, Admin }

    private sealed record Route(string Id, string Method, string Template, bool Gated, Auth Auth, bool PlannerGate = false);

    private static readonly Route[] Contract =
    [
        new("T1", "GET", "/api/v1/trips/{tripId:guid}/taxi-request/pickup-places", false, Auth.Seeker, true),
        new("T2", "POST", "/api/v1/trips/{tripId:guid}/taxi-request", true, Auth.Seeker, true),
        new("T3", "GET", "/api/v1/trips/{tripId:guid}/taxi-request", false, Auth.Seeker, true),
        new("T4", "GET", "/api/v1/taxi-requests/{requestId:guid}", false, Auth.Seeker),
        new("T5", "POST", "/api/v1/taxi-requests/{requestId:guid}/cancel", false, Auth.Seeker),
        new("T6", "POST", "/api/v1/taxi-requests/{requestId:guid}/ask-more", true, Auth.Seeker),
        new("T7", "POST", "/api/v1/taxi-requests/{requestId:guid}/offers/{offerId:guid}/select", true, Auth.Seeker),
        new("T8", "POST", "/api/v1/taxi-requests/{requestId:guid}/offers/{offerId:guid}/not-agreed", false, Auth.Seeker),
        new("T9", "POST", "/api/v1/taxi-requests/{requestId:guid}/end", false, Auth.Seeker),
        new("T10", "POST", "/api/v1/taxi-requests/{requestId:guid}/rating", false, Auth.Seeker),

        new("D1", "GET", "/api/v1/taxi-requests/driver/offers", false, Auth.Taxi),
        new("D2", "GET", "/api/v1/taxi-requests/driver/offers/{offerId:guid}", false, Auth.Taxi),
        new("D3", "POST", "/api/v1/taxi-requests/driver/offers/{offerId:guid}/accept", true, Auth.Taxi),
        new("D4", "POST", "/api/v1/taxi-requests/driver/offers/{offerId:guid}/decline", false, Auth.Taxi),
        new("D5", "POST", "/api/v1/taxi-requests/driver/offers/{offerId:guid}/confirm", true, Auth.Taxi),
        new("D6", "POST", "/api/v1/taxi-requests/driver/offers/{offerId:guid}/withdraw", false, Auth.Taxi),
        new("D7", "POST", "/api/v1/taxi-requests/driver/bookings/{bookingId:guid}/end", false, Auth.Taxi),
        new("D8", "GET", "/api/v1/taxi-requests/driver/bookings", false, Auth.Taxi),
        new("D9", "GET", "/api/v1/taxi-requests/driver/rating", false, Auth.Taxi),
        new("D10", "GET", "/api/v1/taxi-requests/driver/readiness", false, Auth.Taxi),
        new("D11", "PUT", "/api/v1/taxi-requests/driver/readiness", false, Auth.Taxi),
        new("D12", "POST", "/api/v1/taxi-requests/driver/test-ring", false, Auth.Taxi),

        new("ACK", "POST", "/api/v1/taxi-requests/offers/{offerId:guid}/ack", false, Auth.Anonymous),

        new("AD1", "GET", "/api/v1/admin/taxi-requests", false, Auth.Admin),
        new("AD2", "GET", "/api/v1/admin/taxi-requests/{id:guid}", false, Auth.Admin),
        new("AD3", "POST", "/api/v1/admin/taxi-requests/{id:guid}/cancel", false, Auth.Admin),
        new("AD4", "GET", "/api/v1/admin/taxi-bookings", false, Auth.Admin),
        new("AD5", "GET", "/api/v1/admin/taxi-requests/health", false, Auth.Admin),
    ];

    private static readonly string[] MapMethods =
        ["MapTripTaxiRequestEndpoints", "MapTaxiRequestEndpoints", "MapTaxiDriverRequestEndpoints", "MapAdminTaxiRequestEndpoints"];

    private sealed record Built(
        string Template, string[] Methods, MethodInfo Handler, IReadOnlyList<IAuthorizeData> Authorize, bool AllowAnonymous,
        IReadOnlyList<Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate>> Filters);

    private sealed class StubReader(bool enabled) : ITripExecutionFeatureReader
    {
        public Task<bool> IsEnabledAsync() => Task.FromResult(enabled);
        public void Invalidate() { }
    }

    private static (WebApplication App, List<Built> Endpoints) Build()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Host=localhost;Database=test;Username=test;Password=test",
            ["Jwt:Key"] = "unit-test-signing-key-unit-test-signing-key",
        });
        builder.Services.AddApplicationServices(builder.Configuration);
        builder.Services.AddValidatorsFromAssemblyContaining<SendOtpRequestValidator>();
        var app = builder.Build();

        var built = new List<Built>();
        var root = app.MapGroup("");
        root.MapTaxiRequests();
        ((IEndpointConventionBuilder)root).Finally(b =>
        {
            var route = (RouteEndpointBuilder)b;
            built.Add(new Built(
                "/" + route.RoutePattern.RawText!.Trim('/'),
                route.Metadata.OfType<IHttpMethodMetadata>().Last().HttpMethods.ToArray(),
                route.Metadata.OfType<MethodInfo>().Single(),
                route.Metadata.OfType<IAuthorizeData>().ToList(),
                route.Metadata.OfType<IAllowAnonymous>().Any(),
                route.FilterFactories.ToList()));
        });

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).ToList();
        Assert.Equal(built.Count, endpoints.Count);
        return (app, built);
    }

    private static Built Find(List<Built> endpoints, Route row)
        => Assert.Single(endpoints, e => e.Template == row.Template && e.Methods.SequenceEqual([row.Method]));

    private static async Task<object?> RunFiltersAsync(WebApplication app, Built endpoint, bool executionOn, bool plannerOn)
    {
        var factoryContext = new EndpointFilterFactoryContext { MethodInfo = endpoint.Handler, ApplicationServices = app.Services };
        EndpointFilterDelegate next = _ => ValueTask.FromResult<object?>("handler");
        for (var i = endpoint.Filters.Count - 1; i >= 0; i--) next = endpoint.Filters[i](factoryContext, next);

        var services = new ServiceCollection()
            .AddSingleton<ITripExecutionFeatureReader>(new StubReader(executionOn))
            .AddSingleton(Options.Create(new TripPlannerOptions { Enabled = plannerOn }))
            .BuildServiceProvider();
        return await next(EndpointFilterInvocationContext.Create(new DefaultHttpContext { RequestServices = services }));
    }

    private static async Task<string> BodyOf(WebApplication app, object? result)
    {
        var http = new DefaultHttpContext { RequestServices = app.Services };
        http.Response.Body = new MemoryStream();
        await ((IResult)result!).ExecuteAsync(http);
        http.Response.Body.Position = 0;
        return await new StreamReader(http.Response.Body).ReadToEndAsync();
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile)! })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
            if (dir != null) return dir.FullName;
        }

        throw new InvalidOperationException("Repository root not found");
    }

    [Fact]
    public async Task Every_contract_route_is_mapped_once()
    {
        var (app, endpoints) = Build();
        await using var _ = app;

        foreach (var row in Contract) Find(endpoints, row);
        Assert.Equal(Contract.Length, endpoints.Count);

        var api = Path.Combine(RepoRoot(), "RentNearBy.Api");
        var sources = Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => f, File.ReadAllText);

        var routeMap = sources[Path.Combine(api, "Extensions", "ApiRouteMap.cs")];
        Assert.Single(Regex.Matches(routeMap, @"\bMapTaxiRequests\(\)"));
        Assert.Single(sources.Values.SelectMany(s => Regex.Matches(s, @"\.MapTaxiRequests\(\)")));

        var extension = Path.Combine(api, "Extensions", "TaxiRequestServiceCollectionExtensions.cs");
        foreach (var method in MapMethods)
        {
            var calls = sources.Where(s => Regex.IsMatch(s.Value, $@"\.{method}\(\)")).Select(s => s.Key).ToList();
            Assert.Equal([extension], calls);
            Assert.Single(Regex.Matches(sources[extension], $@"\.{method}\(\)"));
        }
    }

    [Fact]
    public async Task Gated_routes_carry_the_gate_and_open_routes_do_not()
    {
        var (app, endpoints) = Build();
        await using var _ = app;

        foreach (var row in Contract)
        {
            var e = Find(endpoints, row);
            Assert.Equal("handler", await RunFiltersAsync(app, e, executionOn: true, plannerOn: true));

            var off = await RunFiltersAsync(app, e, executionOn: false, plannerOn: true);
            if (row.Gated)
            {
                Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(off).StatusCode);
                Assert.Contains(TripEnquiryErrorTypes.TripExecutionDisabled, await BodyOf(app, off));
            }
            else
            {
                Assert.True("handler".Equals(off), $"{row.Id} must stay open while the master switch is off");
            }

            var plannerOff = await RunFiltersAsync(app, e, executionOn: true, plannerOn: false);
            if (row.PlannerGate)
            {
                Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(plannerOff).StatusCode);
                Assert.Contains(TripErrorTypes.TripPlannerDisabled, await BodyOf(app, plannerOff));
            }
            else
            {
                Assert.True("handler".Equals(plannerOff), $"{row.Id} must not carry the planner gate");
            }
        }
    }

    [Fact]
    public async Task Driver_routes_require_the_taxi_policy()
    {
        var (app, endpoints) = Build();
        await using var _ = app;

        foreach (var row in Contract)
        {
            var e = Find(endpoints, row);
            var policies = e.Authorize.Select(a => a.Policy).ToList();
            switch (row.Auth)
            {
                case Auth.Taxi:
                    Assert.NotEmpty(policies);
                    Assert.All(policies, p => Assert.Equal("CanListTaxis", p));
                    Assert.False(e.AllowAnonymous);
                    break;
                case Auth.User:
                    Assert.NotEmpty(policies);
                    Assert.All(policies, Assert.Null);
                    Assert.False(e.AllowAnonymous);
                    break;
                case Auth.Seeker:
                    Assert.Equal([AuthPolicies.SeekerOnly], policies);
                    Assert.False(e.AllowAnonymous);
                    break;
                case Auth.Admin:
                    Assert.NotEmpty(policies);
                    Assert.All(policies, p => Assert.Equal("AdminOnly", p));
                    Assert.False(e.AllowAnonymous);
                    break;
            }
        }
    }

    [Fact]
    public async Task Invite_route_is_mapped_once_seeker_only_and_no_execution_gate()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Host=localhost;Database=test;Username=test;Password=test",
            ["Jwt:Key"] = "unit-test-signing-key-unit-test-signing-key",
        });
        builder.Services.AddApplicationServices(builder.Configuration);
        builder.Services.AddValidatorsFromAssemblyContaining<SendOtpRequestValidator>();
        var app = builder.Build();
        await using var _ = app;

        var built = new List<Built>();
        var trips = app.MapGroup("/api/v1/trips").AddEndpointFilter<RentNearBy.Api.Filters.TripPlannerGateFilter>();
        trips.MapTripEndpoints();
        ((IEndpointConventionBuilder)trips).Finally(b =>
        {
            var route = (RouteEndpointBuilder)b;
            built.Add(new Built(
                "/" + route.RoutePattern.RawText!.Trim('/'),
                route.Metadata.OfType<IHttpMethodMetadata>().Last().HttpMethods.ToArray(),
                route.Metadata.OfType<MethodInfo>().Single(),
                route.Metadata.OfType<IAuthorizeData>().ToList(),
                route.Metadata.OfType<IAllowAnonymous>().Any(),
                route.FilterFactories.ToList()));
        });
        var endpointCount = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).Count();
        Assert.Equal(endpointCount, built.Count);

        var invite = Find(built, new Route("INVITE", "GET", "/api/v1/trips/{id:guid}/invite", false, Auth.Seeker, true));
        Assert.Equal(nameof(RentNearBy.Api.Handlers.TripHandlers.GetInvite), invite.Handler.Name);
        Assert.Equal([AuthPolicies.SeekerOnly], invite.Authorize.Select(a => a.Policy).ToList());
        Assert.False(invite.AllowAnonymous);

        Assert.Equal("handler", await RunFiltersAsync(app, invite, executionOn: false, plannerOn: true));
        var plannerOff = await RunFiltersAsync(app, invite, executionOn: true, plannerOn: false);
        Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(plannerOff).StatusCode);
        Assert.Contains(TripErrorTypes.TripPlannerDisabled, await BodyOf(app, plannerOff));

        var api = Path.Combine(RepoRoot(), "RentNearBy.Api");
        var inviteMaps = Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Sum(f => Regex.Matches(File.ReadAllText(f), @"TripHandlers\.GetInvite\b").Count);
        Assert.Equal(1, inviteMaps);
    }

    [Fact]
    public async Task Ack_route_is_anonymous()
    {
        var (app, endpoints) = Build();
        await using var _ = app;

        var ack = Find(endpoints, Contract.Single(r => r.Id == "ACK"));
        Assert.True(ack.AllowAnonymous);
        Assert.Empty(ack.Authorize);
        Assert.Single(endpoints, e => e.AllowAnonymous);
    }
}

using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Endpoints;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Extensions.Owner;
using RentNearBy.Api.Validators;

namespace RentNearBy.Tests.TaxiAvailability;

// Contract 4.1: the built route table, not the source text. Days off need the driver policy (a seeker or Stay Owner token
// gets 403), the admin popular routes need AdminOnly (a driver or seeker token gets 403), the public popular route has none.
public class TaxiAvailabilityEndpointPolicyTests
{
    private sealed record Built(string Template, string Method, IReadOnlyList<string?> Policies, bool AllowAnonymous);

    private static async Task<List<Built>> BuildAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "Host=localhost;Database=test;Username=test;Password=test",
            ["Jwt:Key"] = "unit-test-signing-key-unit-test-signing-key",
        });
        builder.Services.AddApplicationServices(builder.Configuration);
        builder.Services.AddSignalR();
        builder.Services.AddValidatorsFromAssemblyContaining<SendOtpRequestValidator>();
        await using var app = builder.Build();

        var built = new List<Built>();
        var root = app.MapGroup("");
        root.MapOwnerBe1();
        root.MapGroup("/api/v1/taxis").MapTaxiEndpoints();
        root.MapGroup("/api/v1/admin/taxis").MapAdminTaxiEndpoints();
        ((IEndpointConventionBuilder)root).Finally(b =>
        {
            var route = (RouteEndpointBuilder)b;
            var methods = route.Metadata.OfType<IHttpMethodMetadata>().LastOrDefault()?.HttpMethods ?? [];
            foreach (var m in methods)
                built.Add(new Built("/" + route.RoutePattern.RawText!.Trim('/'), m,
                    route.Metadata.OfType<IAuthorizeData>().Select(a => a.Policy).ToList(),
                    route.Metadata.OfType<IAllowAnonymous>().Any()));
        });
        _ = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).ToList();
        return built;
    }

    private static Built Find(List<Built> routes, string method, string template)
        => Assert.Single(routes, r => r.Method == method && r.Template == template);

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task Days_off_routes_need_the_driver_policy(string method)
    {
        var route = Find(await BuildAsync(), method, "/api/v1/taxis/me/days-off");
        Assert.NotEmpty(route.Policies);
        Assert.All(route.Policies, p => Assert.Equal("CanListTaxis", p));
        Assert.False(route.AllowAnonymous);
    }

    [Theory]
    [InlineData("GET", "/api/v1/admin/taxis/popular-routes")]
    [InlineData("POST", "/api/v1/admin/taxis/popular-routes")]
    [InlineData("PUT", "/api/v1/admin/taxis/popular-routes/order")]
    [InlineData("DELETE", "/api/v1/admin/taxis/popular-routes/{id:guid}")]
    [InlineData("PUT", "/api/v1/admin/taxis/popular-routes/{id:guid}/active")]
    public async Task Admin_popular_routes_need_the_admin_policy(string method, string template)
    {
        var route = Find(await BuildAsync(), method, template);
        Assert.NotEmpty(route.Policies);
        Assert.All(route.Policies, p => Assert.Equal("AdminOnly", p));
        Assert.False(route.AllowAnonymous);
    }

    [Fact]
    public async Task Admin_popular_routes_are_exactly_five_and_the_public_one_has_no_policy()
    {
        var routes = await BuildAsync();
        Assert.Equal(5, routes.Count(r => r.Template.StartsWith("/api/v1/admin/taxis/popular-routes", StringComparison.Ordinal)));
        var open = Find(routes, "GET", "/api/v1/taxis/popular-routes");
        Assert.Empty(open.Policies);
    }
}

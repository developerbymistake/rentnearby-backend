using System.Reflection;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Extensions.Buddy;
using RentNearBy.Api.Validators;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be1;

namespace RentNearBy.Tests.Buddy.Be1;

// Contract 2.1 as mapped by MapBe1Buddies (buddy request routes retired by social D3): every route once, user JWT (admin policy on buddy-hold), and the BE1 services resolve.
public class Be1RouteMapTests
{
    private static readonly (string Method, string Template, bool Admin)[] Contract =
    [
        ("POST", "/api/v1/buddies/find", false),
        ("GET", "/api/v1/buddies", false),
        ("DELETE", "/api/v1/buddies/{userId:guid}", false),
        ("GET", "/api/v1/buddies/summary", false),
        ("POST", "/api/v1/users/{userId:guid}/block", false),
        ("POST", "/api/v1/users/{userId:guid}/unblock", false),
        ("GET", "/api/v1/users/me/blocks", false),
        ("POST", "/api/v1/users/{userId:guid}/report", false),
        ("GET", "/api/v1/report-reasons", false),
        ("GET", "/api/v1/users/{userId:guid}/block-context", false),
        ("PUT", "/api/v1/admin/users/{id:guid}/buddy-hold", true),
    ];

    private sealed record Built(string Template, string[] Methods, IReadOnlyList<IAuthorizeData> Authorize);

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
        root.MapBe1Buddies();
        ((IEndpointConventionBuilder)root).Finally(b =>
        {
            var route = (RouteEndpointBuilder)b;
            built.Add(new Built(
                "/" + route.RoutePattern.RawText!.Trim('/'),
                route.Metadata.OfType<IHttpMethodMetadata>().Last().HttpMethods.ToArray(),
                route.Metadata.OfType<IAuthorizeData>().ToList()));
        });
        _ = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).ToList();
        return (app, built);
    }

    [Fact]
    public async Task Every_be1_route_is_mapped_once_with_auth()
    {
        var (app, endpoints) = Build();
        await using var _ = app;
        Assert.Equal(Contract.Length, endpoints.Count);
        foreach (var (method, template, admin) in Contract)
        {
            var e = Assert.Single(endpoints, x => x.Template == template && x.Methods.SequenceEqual([method]));
            Assert.NotEmpty(e.Authorize);
            Assert.Equal(admin, e.Authorize.Any(a => a.Policy == "AdminOnly"));
            if (!admin) Assert.Equal([AuthPolicies.SeekerOnly], e.Authorize.Select(a => a.Policy).ToList());
        }
        Assert.DoesNotContain(endpoints, e => e.Template.Contains("upi") || e.Template.Contains("link") || e.Template.Contains("share"));
    }

    [Fact]
    public async Task Be1_services_resolve_and_join_the_job_and_deletion_lists()
    {
        var (app, _) = Build();
        await using var __ = app;
        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        Assert.NotNull(sp.GetRequiredService<BuddyFindService>());
        Assert.NotNull(sp.GetRequiredService<BuddyRequestService>());
        Assert.NotNull(sp.GetRequiredService<BuddyListService>());
        Assert.IsType<UserBlockService>(sp.GetRequiredService<IUserBlockService>());
        Assert.Contains(sp.GetServices<IBuddyFeatureJob>(), j => j is BuddyRequestExpiryJob);
        Assert.Contains(sp.GetServices<IAccountDeletionParticipant>(), p => p is BuddyAccountDeletion);
    }
}

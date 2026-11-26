using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Extensions.Buddy;
using RentNearBy.Api.Validators.Buddy;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Buddy.Be2;

public class TripSharingRouteTests
{
    private static List<RouteEndpoint> Endpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IRateLimitService, InMemoryRateLimitService>();
        builder.Services.AddSingleton<IValidator<TripInviteRequest>, TripInviteRequestValidator>();
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.MapBe2Sharing();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    [Fact]
    public void Every_sharing_route_is_mapped_once_with_auth()
    {
        string[] expected =
        [
            "GET /api/v1/trips/shared", "GET /api/v1/trips/slots", "GET /api/v1/trips/invite-options", "GET /api/v1/trips/invites/{memberId:guid}",
            "POST /api/v1/trips/invites/{memberId:guid}/accept", "POST /api/v1/trips/invites/{memberId:guid}/decline",
            "GET /api/v1/trips/{id:guid}/members", "DELETE /api/v1/trips/{id:guid}/members/{userId:guid}",
            "GET /api/v1/trips/{id:guid}/members/{userId:guid}/expense-count", "POST /api/v1/trips/{id:guid}/invites",
            "DELETE /api/v1/trips/{id:guid}/invites/{userId:guid}", "POST /api/v1/trips/{id:guid}/leave", "GET /api/v1/trips/{id:guid}/view",
        ];
        var endpoints = Endpoints();
        var routes = endpoints.Select(e =>
            $"{e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText}").ToList();
        Assert.Equal(expected.OrderBy(x => x), routes.OrderBy(x => x));
        Assert.All(endpoints, e => Assert.NotEmpty(e.Metadata.GetOrderedMetadata<IAuthorizeData>()));
        Assert.All(endpoints, e => Assert.Equal([AuthPolicies.SeekerOnly], e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList()));
        Assert.All(routes, r => Assert.DoesNotContain("upi", r, StringComparison.OrdinalIgnoreCase));
        Assert.All(routes, r => Assert.DoesNotContain("link", r, StringComparison.OrdinalIgnoreCase));
    }
}

using System.Reflection;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Endpoints;
using RentNearBy.Api.Handlers.TaxiRequests;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// Contract section 5: traveller routes, methods, auth and the master switch (T2, T6, T7 gated), and the owner check
// running before any validation or service work in T1 to T10.
public class TaxiRequestEndpointsTests
{
    private sealed class StubReader(bool enabled) : ITripExecutionFeatureReader
    {
        public Task<bool> IsEnabledAsync() => Task.FromResult(enabled);
        public void Invalidate() { }
    }

    private sealed record Built(string Route, string[] Methods, MethodInfo Handler, bool Authorized, IReadOnlyList<Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate>> Filters);

    private static readonly (string Method, string Route, string Handler, bool Gated)[] Table =
    [
        ("GET", "/api/v1/trips/{tripId:guid}/taxi-request/pickup-places", nameof(TaxiRequestHandlers.PickupPlaces), false),
        ("POST", "/api/v1/trips/{tripId:guid}/taxi-request", nameof(TaxiRequestHandlers.Create), true),
        ("GET", "/api/v1/trips/{tripId:guid}/taxi-request", nameof(TaxiRequestHandlers.GetForTrip), false),
        ("GET", "/api/v1/taxi-requests/{requestId:guid}", nameof(TaxiRequestHandlers.Get), false),
        ("POST", "/api/v1/taxi-requests/{requestId:guid}/cancel", nameof(TaxiRequestHandlers.Cancel), false),
        ("POST", "/api/v1/taxi-requests/{requestId:guid}/ask-more", nameof(TaxiRequestHandlers.AskMore), true),
        ("POST", "/api/v1/taxi-requests/{requestId:guid}/offers/{offerId:guid}/select", nameof(TaxiRequestHandlers.Select), true),
        ("POST", "/api/v1/taxi-requests/{requestId:guid}/offers/{offerId:guid}/not-agreed", nameof(TaxiRequestHandlers.NotAgreed), false),
        ("POST", "/api/v1/taxi-requests/{requestId:guid}/end", nameof(TaxiRequestHandlers.End), false),
        ("POST", "/api/v1/taxi-requests/{requestId:guid}/rating", nameof(TaxiRequestHandlers.Rate), false),
    ];

    private static async Task<(WebApplication App, List<Built> Endpoints)> BuildAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        static T Unused<T>(IServiceProvider _) => throw new InvalidOperationException("not used");
        builder.Services.AddScoped(Unused<ApplicationDbContext>);
        builder.Services.AddScoped(Unused<TaxiPickupPlaces>);
        builder.Services.AddScoped(Unused<TaxiRequestService>);
        builder.Services.AddScoped(Unused<TaxiRequestViews>);
        builder.Services.AddScoped(Unused<TaxiBookingService>);
        builder.Services.AddScoped(Unused<IValidator<CreateTaxiRequestDto>>);
        builder.Services.AddScoped(Unused<IValidator<CancelTaxiRequestDto>>);
        builder.Services.AddScoped(Unused<IValidator<RateTaxiRequestDto>>);
        builder.Services.AddSingleton<ITripExecutionFeatureReader>(new StubReader(true));
        var app = builder.Build();

        var built = new List<Built>();
        void Capture(EndpointBuilder b)
        {
            var route = (RouteEndpointBuilder)b;
            built.Add(new Built(
                "/" + route.RoutePattern.RawText!.Trim('/'),
                route.Metadata.OfType<IHttpMethodMetadata>().Last().HttpMethods.ToArray(),
                route.Metadata.OfType<MethodInfo>().Single(),
                route.Metadata.OfType<IAuthorizeData>().Any(),
                route.FilterFactories.ToList()));
        }

        var trips = app.MapGroup("/api/v1/trips");
        trips.MapTripTaxiRequestEndpoints();
        ((IEndpointConventionBuilder)trips).Finally(Capture);
        var requests = app.MapGroup("/api/v1/taxi-requests");
        requests.MapTaxiRequestEndpoints();
        ((IEndpointConventionBuilder)requests).Finally(Capture);

        _ = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).ToList();
        await Task.CompletedTask;
        return (app, built);
    }

    private static async Task<object?> RunFiltersAsync(WebApplication app, Built endpoint, bool switchOn)
    {
        var factoryContext = new EndpointFilterFactoryContext { MethodInfo = endpoint.Handler, ApplicationServices = app.Services };
        EndpointFilterDelegate next = _ => ValueTask.FromResult<object?>("handler");
        for (var i = endpoint.Filters.Count - 1; i >= 0; i--) next = endpoint.Filters[i](factoryContext, next);

        var services = new ServiceCollection().AddSingleton<ITripExecutionFeatureReader>(new StubReader(switchOn)).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        return await next(EndpointFilterInvocationContext.Create(http));
    }

    [Fact]
    public async Task Routes_and_methods_match_the_contract_table()
    {
        var (app, endpoints) = await BuildAsync();
        await using var _ = app;

        Assert.Equal(Table.Length, endpoints.Count);
        foreach (var row in Table)
        {
            var e = Assert.Single(endpoints, x => x.Route == row.Route && x.Methods.SequenceEqual([row.Method]));
            Assert.Equal(row.Handler, e.Handler.Name);
            Assert.Equal(typeof(TaxiRequestHandlers), e.Handler.DeclaringType);
            Assert.True(e.Authorized, $"{row.Method} {row.Route} must require authorization");
        }
    }

    [Fact]
    public async Task Switch_off_refuses_T2_T6_T7_only()
    {
        var (app, endpoints) = await BuildAsync();
        await using var _ = app;

        foreach (var row in Table)
        {
            var e = endpoints.Single(x => x.Route == row.Route && x.Methods.SequenceEqual([row.Method]));
            Assert.Equal("handler", await RunFiltersAsync(app, e, switchOn: true));

            var off = await RunFiltersAsync(app, e, switchOn: false);
            if (!row.Gated)
            {
                Assert.Equal("handler", off);
                continue;
            }

            var result = Assert.IsAssignableFrom<IStatusCodeHttpResult>(off);
            Assert.Equal(403, result.StatusCode);
            var http = new DefaultHttpContext { RequestServices = app.Services };
            http.Response.Body = new MemoryStream();
            await ((IResult)off!).ExecuteAsync(http);
            http.Response.Body.Position = 0;
            Assert.Contains(TripEnquiryErrorTypes.TripExecutionDisabled, await new StreamReader(http.Response.Body).ReadToEndAsync());
        }
    }

    private static readonly (string Handler, string OwnerCheck)[] OwnerChecks =
    [
        (nameof(TaxiRequestHandlers.PickupPlaces), "await CheckTripOwnerAsync(db, tripId, userId, ct) is { } denied"),
        (nameof(TaxiRequestHandlers.Create), "await CheckTripOwnerAsync(db, tripId, userId, ct) is { } denied"),
        (nameof(TaxiRequestHandlers.GetForTrip), "await CheckTripOwnerAsync(db, tripId, userId, ct) is { } denied"),
        (nameof(TaxiRequestHandlers.Get), "return await RespondAsync(await service.OwnedAsync(requestId, userId, ct), views, http, ct);"),
        (nameof(TaxiRequestHandlers.Cancel), "await service.OwnedAsync(requestId, userId, ct) is { IsOk: false } denied"),
        (nameof(TaxiRequestHandlers.AskMore), "await service.OwnedAsync(requestId, userId, ct) is { IsOk: false } denied"),
        (nameof(TaxiRequestHandlers.Select), "await service.OwnedAsync(requestId, userId, ct) is { IsOk: false } denied"),
        (nameof(TaxiRequestHandlers.NotAgreed), "await service.OwnedAsync(requestId, userId, ct) is { IsOk: false } denied"),
        (nameof(TaxiRequestHandlers.End), "await service.OwnedAsync(requestId, userId, ct) is { IsOk: false } denied"),
        (nameof(TaxiRequestHandlers.Rate), "await service.OwnedAsync(requestId, userId, ct) is { IsOk: false } denied"),
    ];

    // Everything a handler may do with the caller's input once the owner is known.
    private static readonly string[] Work = ["ValidateAsync(", "service.", "places.", "bookings.", "views.", "db.", "gate.", "RespondAsync("];

    [Fact]
    public void Every_routed_handler_has_an_owner_check_entry()
    {
        var routed = Table.Select(r => r.Handler).OrderBy(n => n).ToArray();
        Assert.Equal(routed, OwnerChecks.Select(o => o.Handler).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Owner_check_runs_before_any_validation_or_service_work()
    {
        var source = TaxiSource.Read("RentNearBy.Api", "Handlers", "TaxiRequests", "TaxiRequestHandlers.cs");
        foreach (var (handler, ownerCheck) in OwnerChecks)
        {
            var body = TaxiSource.Member(source, $"public static async Task<IResult> {handler}(");
            body = body[body.IndexOf("{", StringComparison.Ordinal)..];
            var owner = TaxiSource.At(body, ownerCheck);
            var auth = TaxiSource.At(body, "if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();");
            Assert.True(auth < owner, $"{handler}: user id must be read before the owner check");

            // The owner check itself may contain "service." (OwnedAsync); nothing else may come before it.
            foreach (var marker in Work)
            {
                for (var i = body.IndexOf(marker, StringComparison.Ordinal); i >= 0; i = body.IndexOf(marker, i + 1, StringComparison.Ordinal))
                {
                    var insideOwnerCheck = i >= owner && i < owner + ownerCheck.Length;
                    Assert.True(insideOwnerCheck || i > owner + ownerCheck.Length, $"{handler}: '{marker}' at {i} runs before the owner check at {owner}");
                }
            }
        }
    }
}

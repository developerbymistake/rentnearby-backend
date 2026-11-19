using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Api.Handlers.TaxiRequests;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

// Needs PostgreSQL (RENTNEARBY_TEST_DB); skipped without it. The handlers run directly with a real context.
[Trait("Category", "Integration")]
public class TaxiRequestApiIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static ClaimsPrincipal Caller(Guid userId)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));

    private static HttpContext Http()
    {
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        return new DefaultHttpContext { RequestServices = services, Response = { Body = new MemoryStream() } };
    }

    private static async Task<(int Status, string? ErrorType)> RunAsync(IResult result)
    {
        var http = Http();
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();
        if (text.Length == 0) return (http.Response.StatusCode, null);
        using var doc = JsonDocument.Parse(text);
        var error = doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e.GetProperty("type").GetString() : null;
        return (http.Response.StatusCode, error);
    }

    private static TaxiRequestService Service(ApplicationDbContext db)
        => new(db, null!, null!, null!, null!, null!, NullLogger<TaxiRequestService>.Instance);

    private static async Task<Guid> RequestOfAsync(JourneyHarness h, Guid trip, Guid traveller)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiRequest>().Add(new TaxiRequest
        {
            Id = id, TripId = trip, TravellerUserId = traveller, Status = RequestStates.Open, WantPickup = true, PickupCityKey = 1, PickupCityName = "A",
            DispatchCityKey = 1, MatchToCityKey = 2, Seats = 4, Travellers = 2, DateFrom = h.Start, DateTo = h.Start.AddDays(2),
        });
        await s.Db.SaveChangesAsync();
        return id;
    }

    [IntegrationFact]
    public async Task Other_users_request_is_403()
    {
        await using var h = new JourneyHarness(Db);
        var owner = await h.AddSeekerAsync();
        var stranger = await h.AddSeekerAsync();
        var trip = await h.AddTripAsync(owner, h.BuildPlan(("Nainital", 210000001, 3)));
        var request = await RequestOfAsync(h, trip, owner);

        await using var s = h.NewScope();
        var service = Service(s.Db);
        var views = new TaxiRequestViews(s.Db);
        var http = Http();
        var limiter = new InMemoryRateLimitService();

        Assert.Equal((403, TaxiRequestErrorTypes.NotTripOwner),
            await RunAsync(await TaxiRequestHandlers.Get(request, Caller(stranger), service, views, http, limiter, default)));
        Assert.Equal((403, TaxiRequestErrorTypes.NotTripOwner),
            await RunAsync(await TaxiRequestHandlers.Cancel(request, new CancelTaxiRequestDto { Reason = RequestEndReasons.TravellerCancelled },
                new CancelTaxiRequestValidator(), Caller(stranger), service, views, http, limiter, default)));
        Assert.Equal((403, TaxiRequestErrorTypes.NotTripOwner),
            await RunAsync(await TaxiRequestHandlers.GetForTrip(trip, Caller(stranger), s.Db, views, http, limiter, default)));
        Assert.Equal((200, null), await RunAsync(await TaxiRequestHandlers.Get(request, Caller(owner), service, views, http, limiter, default)));

        await using var read = CreateContext();
        Assert.Equal(RequestStates.Open, (await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == request)).Status);
    }

    [IntegrationFact]
    public async Task Unknown_request_is_404()
    {
        await using var h = new JourneyHarness(Db);
        var user = await h.AddSeekerAsync();
        await using var s = h.NewScope();
        var result = await TaxiRequestHandlers.Get(Guid.NewGuid(), Caller(user), Service(s.Db), new TaxiRequestViews(s.Db), Http(), new InMemoryRateLimitService(), default);
        Assert.Equal((404, TaxiRequestErrorTypes.TaxiRequestNotFound), await RunAsync(result));
    }
}

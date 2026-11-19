using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

// Needs PostgreSQL (RENTNEARBY_TEST_DB); skipped without it.
[Trait("Category", "Integration")]
public class TripInviteIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static ClaimsPrincipal Caller(Guid userId)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));

    private static IConfiguration Config()
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [TripInvites.ConfigKey] = "https://x.test" }).Build();

    private static async Task<(int Status, JsonElement Body)> RunAsync(IResult result)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(http.Response.Body);
        return (http.Response.StatusCode, doc.RootElement.Clone());
    }

    private static async Task<(Guid Owner, Guid Trip, Guid Code)> TripAsync(JourneyHarness h)
    {
        var owner = await h.AddSeekerAsync();
        var trip = await h.AddTripAsync(owner, h.BuildPlan(("Nainital", 210000001, 3)));
        var code = Guid.NewGuid();
        await using var s = h.NewScope();
        await s.Db.Trips.Where(t => t.Id == trip).ExecuteUpdateAsync(u => u.SetProperty(t => t.InviteCode, code));
        return (owner, trip, code);
    }

    [IntegrationFact]
    public async Task Owner_gets_the_code()
    {
        await using var h = new JourneyHarness(Db);
        var (owner, trip, code) = await TripAsync(h);

        await using var s = h.NewScope();
        var (status, body) = await RunAsync(await TripHandlers.GetInvite(trip, Caller(owner), s.Uow, Config()));

        Assert.Equal(200, status);
        var data = body.GetProperty("data");
        Assert.Equal(trip, data.GetProperty("tripId").GetGuid());
        Assert.Equal(code, data.GetProperty("inviteCode").GetGuid());
        Assert.Equal($"https://x.test/app?invite={code}", data.GetProperty("inviteLink").GetString());
    }

    [IntegrationFact]
    public async Task Non_owner_gets_404()
    {
        await using var h = new JourneyHarness(Db);
        var (_, trip, code) = await TripAsync(h);
        var stranger = await h.AddSeekerAsync();

        await using var s = h.NewScope();
        var (status, body) = await RunAsync(await TripHandlers.GetInvite(trip, Caller(stranger), s.Uow, Config()));

        Assert.Equal(404, status);
        Assert.Equal(TaxiRequestErrorTypes.NotFound, body.GetProperty("error").GetProperty("type").GetString());
        Assert.DoesNotContain(code.ToString(), body.GetRawText());
    }

    [IntegrationFact]
    public async Task Unknown_trip_gets_404()
    {
        await using var h = new JourneyHarness(Db);
        var owner = await h.AddSeekerAsync();

        await using var s = h.NewScope();
        var (status, _) = await RunAsync(await TripHandlers.GetInvite(Guid.NewGuid(), Caller(owner), s.Uow, Config()));

        Assert.Equal(404, status);
    }
}

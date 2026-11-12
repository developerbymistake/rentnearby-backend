using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Tests.Trips;

public class TaxiWaysLegDriversHandlerTests
{
    private static readonly Guid User = Guid.NewGuid();
    private const string Leg1Key = "l1~a210000001~a210000002";
    private const string Leg2Key = "l1~a210000002~a210000003";
    private static readonly Guid Leg1Route = Guid.NewGuid();

    private sealed class FakeSearch : ITaxiWaysSearch
    {
        public LegDriverQuery? Seen;
        public string? SeenKey;
        public WayDriverPageDto? Result = new() { Total = 1 };
        public bool Throw;

        public Task<TaxiWaysResultDto?> SearchAsync(TaxiWaysQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WayPlanDetailDto?> PlanAsync(string planKey, int people, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WayDriverPageDto?> LegDriversAsync(string legKey, LegDriverQuery q, CancellationToken ct = default)
        {
            if (Throw) throw new TaxiWaysUnavailableException();
            SeenKey = legKey;
            Seen = q;
            return Task.FromResult(Result);
        }

        public Task<Dictionary<Guid, LegTaxiFare>> LegFaresAsync(
            string legKey, IReadOnlyCollection<Guid> taxiIds, Guid? excludeRoute = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class AllowAll : IRateLimitService
    {
        public Task<RateLimitResult> CheckAsync(string key, int maxAttempts, TimeSpan window, int permits = 1) =>
            Task.FromResult(new RateLimitResult(true, maxAttempts, null));
        public Task ReleaseAsync(string key, string token) => Task.CompletedTask;
    }

    // Only GetOwnedAsync is used by the handler; every other member of the repository fails loudly.
    public class JourneyStub : DispatchProxy
    {
        public SavedJourney? Journey;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ISavedJourneyRepository.GetOwnedAsync)) throw new NotSupportedException(targetMethod?.Name);
            var found = Journey != null && Journey.Id == (Guid)args![0]! && Journey.UserId == (Guid)args[1]! ? Journey : null;
            return Task.FromResult(found);
        }
    }

    private static ISavedJourneyRepository Repo(SavedJourney? journey)
    {
        var proxy = DispatchProxy.Create<ISavedJourneyRepository, JourneyStub>();
        ((JourneyStub)(object)proxy).Journey = journey;
        return proxy;
    }

    private static SavedJourney Journey(Guid owner) => new()
    {
        Id = Guid.NewGuid(),
        UserId = owner,
        Legs =
        [
            new SavedJourneyLeg { LegIndex = 1, LegKey = Leg1Key, DriverRouteId = Leg1Route },
            new SavedJourneyLeg { LegIndex = 2, LegKey = Leg2Key, DriverRouteId = Guid.NewGuid() },
        ],
    };

    private static ClaimsPrincipal Caller(Guid? id) =>
        new(new ClaimsIdentity(id == null ? [] : [new Claim(ClaimTypes.NameIdentifier, id.Value.ToString())], "test"));

    private static async Task<(int Status, string? ErrorType)> CallAsync(
        FakeSearch search, ISavedJourneyRepository repo, string legKey, Guid? journeyId = null, int? legIndex = null,
        Guid? afterRoute = null, bool? lastLeg = null, Guid? caller = null)
    {
        var result = await TaxiWaysHandlers.GetLegDrivers(
            legKey, 2, null, null, null, null, search, repo, new AllowAll(), Caller(caller ?? User), new DefaultHttpContext(),
            Options.Create(new TaxiWaysOptions()), CancellationToken.None, 1, 0, journeyId, legIndex, afterRoute, lastLeg);
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        var body = (result as IValueHttpResult)?.Value as ApiResponse<object>;
        return (status, body?.Error?.Type);
    }

    [Fact]
    public async Task Leg_two_of_a_saved_journey_excludes_the_route_of_leg_one()
    {
        var journey = Journey(User);
        var search = new FakeSearch();

        var (status, _) = await CallAsync(search, Repo(journey), Leg2Key, journey.Id, 2);

        Assert.Equal(200, status);
        Assert.Equal(Leg2Key, search.SeenKey);
        Assert.Equal(Leg1Route, search.Seen!.ExcludeRoute);
    }

    [Fact]
    public async Task Leg_one_of_a_saved_journey_excludes_nothing()
    {
        var journey = Journey(User);
        var search = new FakeSearch();

        await CallAsync(search, Repo(journey), Leg1Key, journey.Id, 1);

        Assert.Null(search.Seen!.ExcludeRoute);
    }

    [Fact]
    public async Task A_leg_key_that_is_not_that_leg_of_the_journey_is_not_found()
    {
        var journey = Journey(User);
        var search = new FakeSearch();

        var (status, type) = await CallAsync(search, Repo(journey), Leg1Key, journey.Id, 2);

        Assert.Equal(404, status);
        Assert.Equal(TaxiWaysErrorTypes.LegNotFound, type);
        Assert.Null(search.Seen);
    }

    [Fact]
    public async Task A_journey_of_another_user_is_not_found()
    {
        var journey = Journey(Guid.NewGuid());
        var search = new FakeSearch();

        var (status, type) = await CallAsync(search, Repo(journey), Leg2Key, journey.Id, 2);

        Assert.Equal(404, status);
        Assert.Equal(TaxiWaysErrorTypes.LegNotFound, type);
        Assert.Null(search.Seen);
    }

    [Fact]
    public async Task An_unsaved_plan_passes_after_route_through()
    {
        var after = Guid.NewGuid();
        var search = new FakeSearch();

        var (status, _) = await CallAsync(search, Repo(null), Leg2Key, afterRoute: after, lastLeg: true);

        Assert.Equal(200, status);
        Assert.Equal(after, search.Seen!.ExcludeRoute);
    }

    [Fact]
    public async Task A_journey_id_without_a_leg_index_is_a_bad_request()
    {
        var search = new FakeSearch();

        var (status, _) = await CallAsync(search, Repo(Journey(User)), Leg2Key, Guid.NewGuid(), null);

        Assert.Equal(400, status);
        Assert.Null(search.Seen);
    }

    [Fact]
    public async Task No_network_answers_503()
    {
        var search = new FakeSearch { Throw = true };

        var (status, type) = await CallAsync(search, Repo(null), Leg1Key);

        Assert.Equal(503, status);
        Assert.Equal(TaxiWaysErrorTypes.TaxiWaysUnavailable, type);
    }

    [Fact]
    public async Task No_driver_left_on_the_stretch_is_not_found()
    {
        var search = new FakeSearch { Result = null };

        var (status, type) = await CallAsync(search, Repo(null), Leg1Key);

        Assert.Equal(404, status);
        Assert.Equal(TaxiWaysErrorTypes.LegNotFound, type);
    }

    [Fact]
    public async Task A_caller_without_a_user_id_is_unauthorized()
    {
        var result = await TaxiWaysHandlers.GetLegDrivers(
            Leg1Key, 2, null, null, null, null, new FakeSearch(), Repo(null), new AllowAll(), Caller(null), new DefaultHttpContext(),
            Options.Create(new TaxiWaysOptions()), CancellationToken.None);

        Assert.Equal(401, ((IStatusCodeHttpResult)result).StatusCode);
    }
}

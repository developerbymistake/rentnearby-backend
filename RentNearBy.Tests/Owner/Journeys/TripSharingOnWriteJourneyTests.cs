using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// JT-B-SK-1 / FB-01: create, replay, plan save and complete-plan answers carry the sharing block.
public class TripSharingOnWriteGuardTests
{
    private static string Handler() => File.ReadAllText(Path.Combine(CompletionCounterGuardTests.Root(), "RentNearBy.Api", "Handlers", "TripHandlers.cs"));

    private static string Body(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start > 0, signature);
        var next = src.IndexOf("    public static async Task<IResult> ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? src[start..] : src[start..next];
    }

    [Theory]
    [InlineData("public static async Task<IResult> Create(")]
    [InlineData("public static async Task<IResult> Update(")]
    [InlineData("public static async Task<IResult> CompletePlan(")]
    [InlineData("public static async Task<IResult> GetById(")]
    public void Every_owner_trip_answer_sets_the_sharing_block(string signature)
        => Assert.Contains(".Sharing = await sharingHooks.", Body(Handler(), signature));

    [Fact]
    public void Create_answers_never_build_a_dto_without_sharing()
    {
        var create = Body(Handler(), "public static async Task<IResult> Create(");
        // The three replays go through Replay(); the 201 uses the one-statement new-trip read.
        Assert.Equal(3, create.Split("return await Replay(").Length - 1);
        Assert.Contains("GetNewTripSharingAsync", create);
        Assert.DoesNotContain("return OkResponse(await BuildDtoAsync", create);
    }
}

// Integration (WRITTEN, NOT RUN: needs RENTNEARBY_TEST_DB with PostGIS).
public class TripSharingOnWriteJourneyTests(TestDatabase db) : IntegrationTestBase(db)
{
    private sealed class FakeHooks : ITripSharingHooks
    {
        public int Full;
        public int New;

        private static TripSharingBlockDto Alone() => new() { TravellerCount = 1 };

        public Task<TripSharingBlockDto?> GetSharingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default)
        {
            Full++;
            return Task.FromResult<TripSharingBlockDto?>(Alone());
        }

        public Task<TripSharingBlockDto?> GetNewTripSharingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default)
        {
            New++;
            return Task.FromResult<TripSharingBlockDto?>(Alone());
        }

        public Task OnPlanSavedAsync(Guid tripId, Guid ownerId, int? changedDay = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> OnTripDeletingAsync(Guid tripId, Guid ownerId, CancellationToken ct = default) => Task.FromResult(0);
    }

    [IntegrationFact]
    public async Task Complete_plan_and_plan_save_answers_carry_sharing()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var trip = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));
        var hooks = new FakeHooks();

        var (_, plan, _) = await h.LoadAsync(trip);
        await using (var s = h.NewScope())
        {
            var saved = await TripHandlers.Update(trip, new SaveTripRequest { Plan = plan }, new SaveTripRequestValidator(),
                JourneyHarness.Principal(seeker), s.Uow, s.Db, h.Chat, h.Notifier, h.Loggers, s.Journeys, new NoCatalogStub(), hooks);
            Assert.NotNull(JourneyHarness.DataOf<TripDto>(saved).Sharing);
        }
        await using (var s = h.NewScope())
        {
            var done = await TripHandlers.CompletePlan(trip, JourneyHarness.Principal(seeker), s.Uow, s.Db, s.Journeys, hooks);
            Assert.NotNull(JourneyHarness.DataOf<TripDto>(done).Sharing);
        }
        Assert.Equal(2, hooks.Full);
        Assert.Equal(0, hooks.New);
    }

    private sealed class NoCatalogStub : ITripCityCatalog
    {
        public Task<TripCityCatalogSnapshot?> GetAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(bool Available, TripCityInfo? City)> FindAsync(long cityKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task InvalidateAsync() => throw new NotSupportedException();
    }
}

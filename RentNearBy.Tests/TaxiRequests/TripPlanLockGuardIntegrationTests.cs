using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

// Written for a real PostgreSQL (RENTNEARBY_TEST_DB); skipped without one.
[Trait("Category", "Integration")]
public class TripPlanLockGuardIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    private const long Nainital = 210000001;
    private const long Ranikhet = 210000002;

    private static async Task<Guid> AddRequestAsync(JourneyHarness h, Guid tripId, Guid travellerId, string status)
    {
        var id = Guid.NewGuid();
        await using var s = h.NewScope();
        s.Db.Set<TaxiRequest>().Add(Request(id, tripId, travellerId, status));
        await s.Db.SaveChangesAsync();
        return id;
    }

    private static TaxiRequest Request(Guid id, Guid tripId, Guid travellerId, string status) => new()
    {
        Id = id, TripId = tripId, TravellerUserId = travellerId, Status = status, WantSightseeing = true,
        DispatchCityKey = Nainital, MatchToCityKey = Nainital, Seats = 4, Travellers = 2,
        DateFrom = new DateOnly(2026, 12, 18), DateTo = new DateOnly(2026, 12, 20),
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static async Task SetRequestStatusAsync(JourneyHarness h, Guid requestId, string status)
    {
        await using var s = h.NewScope();
        await s.Db.Set<TaxiRequest>().Where(r => r.Id == requestId)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, status).SetProperty(r => r.ClosedAt, DateTime.UtcNow));
    }

    private static ApiResponse<object> Body(IResult result) => (ApiResponse<object>)((IValueHttpResult)result).Value!;

    private static async Task<IResult> DeleteAsync(JourneyHarness h, Guid tripId, Guid callerId)
    {
        await using var s = h.NewScope();
        return await TripHandlers.Delete(tripId, JourneyHarness.Principal(callerId), s.Uow, s.Closure, s.Journeys, s.Db);
    }

    [IntegrationFact]
    public async Task Locked_structural_put_is_409_and_saves_nothing()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", Nainital, 3)));
        var requestId = await AddRequestAsync(h, tripId, seeker, RequestStates.Collecting);
        var before = (await h.LoadAsync(tripId)).Trip;

        var result = await h.UpdatePlanAsync(tripId, seeker, p => p.Days[0].Spots.Clear());

        Assert.Equal(409, JourneyHarness.StatusOf(result));
        var body = Body(result);
        Assert.Equal(TaxiRequestErrorTypes.PlanLocked, body.Error!.Type);
        Assert.Equal(PlanLockReasons.MessageFor(PlanLockReasons.TaxiRequestActive), body.Error.Message);
        var data = Assert.IsType<PlanLockedData>(body.Data);
        Assert.Equal(PlanLockReasons.TaxiRequestActive, data.Reason);
        Assert.Equal(requestId, data.RequestId);
        var after = (await h.LoadAsync(tripId)).Trip;
        Assert.Equal(before.PlanJson, after.PlanJson);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [IntegrationFact]
    public async Task Locked_non_structural_put_saves()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", Nainital, 3)));
        await AddRequestAsync(h, tripId, seeker, RequestStates.Booked);

        var result = await h.UpdatePlanAsync(tripId, seeker, p =>
        {
            p.Guide = new TripGuide { Overview = true };
            p.Steps!.GettingThere = new TripStep { Status = TripStepStatuses.Own };
        });

        Assert.Equal(200, JourneyHarness.StatusOf(result));
        var plan = (await h.LoadAsync(tripId)).Plan;
        Assert.True(plan.Guide?.Overview);
        Assert.Equal(TripStepStatuses.Own, plan.Steps!.GettingThere!.Status);
    }

    [IntegrationFact]
    public async Task Unlocked_structural_put_still_saves()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", Nainital, 3)));
        var requestId = await AddRequestAsync(h, tripId, seeker, RequestStates.Open);
        await SetRequestStatusAsync(h, requestId, RequestStates.Cancelled);

        var result = await h.UpdatePlanAsync(tripId, seeker, p => p.Days[0].Spots.Clear());

        Assert.Equal(200, JourneyHarness.StatusOf(result));
        Assert.Empty((await h.LoadAsync(tripId)).Plan.Days[0].Spots);
    }

    [IntegrationFact]
    public async Task Create_request_racing_an_update_ends_consistent()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", Nainital, 3)));
        var before = (await h.LoadAsync(tripId)).Trip.PlanJson;

        // The create holds the trip row lock (as TaxiRequestService does) while the update starts.
        await using var create = h.NewScope();
        await using var tx = await create.Db.Database.BeginTransactionAsync();
        await TripGuards.LockTripAsync(create.Db, tripId);
        create.Db.Set<TaxiRequest>().Add(Request(Guid.NewGuid(), tripId, seeker, RequestStates.Open));
        await create.Db.SaveChangesAsync();

        var update = Task.Run(() => h.UpdatePlanAsync(tripId, seeker, p => p.Days[1].Spots.Clear()));
        await Task.Delay(500);
        Assert.False(update.IsCompleted, "The update must wait for the trip row lock");
        await tx.CommitAsync();

        var result = await update;
        Assert.Equal(409, JourneyHarness.StatusOf(result));
        Assert.Equal(TaxiRequestErrorTypes.PlanLocked, Body(result).Error!.Type);
        Assert.Equal(before, (await h.LoadAsync(tripId)).Trip.PlanJson);
    }

    [IntegrationFact]
    public async Task Delete_is_blocked_while_active_and_allowed_after_cancel()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", Nainital, 3)));
        var requestId = await AddRequestAsync(h, tripId, seeker, RequestStates.Selected);

        var blocked = await DeleteAsync(h, tripId, seeker);
        Assert.Equal(409, JourneyHarness.StatusOf(blocked));
        Assert.Equal(TaxiRequestErrorTypes.PlanLocked, Body(blocked).Error!.Type);
        Assert.Equal("Cancel the taxi request first.", Body(blocked).Error!.Message);
        await using (var read = CreateContext())
            Assert.True(await read.Trips.AnyAsync(t => t.Id == tripId));

        await SetRequestStatusAsync(h, requestId, RequestStates.Cancelled);
        var deleted = await DeleteAsync(h, tripId, seeker);
        Assert.Equal(200, JourneyHarness.StatusOf(deleted));
        await using var check = CreateContext();
        Assert.False(await check.Trips.AnyAsync(t => t.Id == tripId));
    }

    [IntegrationFact]
    public async Task Legacy_lead_create_is_refused_while_a_request_is_active()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var taxi = await h.AddTaxiAsync(await h.AddTaxiDriverAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", Nainital, 3)));
        await AddRequestAsync(h, tripId, seeker, RequestStates.Open);

        var result = await h.AskAsync(tripId, seeker, new CreateTripEnquiryRequest
        {
            Kind = TripEnquiryKinds.Taxi, Days = [1], ListingIds = [taxi], PickupTime = "09:00",
        });

        Assert.Equal(409, JourneyHarness.StatusOf(result));
        Assert.Equal(TaxiRequestErrorTypes.TaxiRequestActive, Body(result).Error!.Type);
        Assert.Empty((await h.LoadAsync(tripId)).Enquiries);
    }

    [IntegrationFact]
    public async Task Legacy_local_booking_is_refused_while_a_request_is_active()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var taxi = await h.AddTaxiAsync(await h.AddTaxiDriverAsync());
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", Nainital, 3)));
        var requestId = await AddRequestAsync(h, tripId, seeker, RequestStates.InProgress);
        var booking = new CreateTaxiBookingRequest { TaxiId = taxi, Days = [1, 2], PickupTime = "09:00", AgreedFare = 3000 };

        var refused = await h.TaxiBookAsync(tripId, seeker, booking);
        Assert.Equal(409, JourneyHarness.StatusOf(refused));
        Assert.Equal(TaxiRequestErrorTypes.TaxiRequestActive, Body(refused).Error!.Type);
        Assert.Empty((await h.LoadAsync(tripId)).Enquiries);

        await SetRequestStatusAsync(h, requestId, RequestStates.Completed);
        var allowed = await h.TaxiBookAsync(tripId, seeker, booking);
        Assert.Equal(200, JourneyHarness.StatusOf(allowed));
    }

    [IntegrationFact]
    public async Task Journey_car_booking_still_works_while_a_request_is_active()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var driver = await h.AddTaxiDriverAsync();
        var taxi = await h.AddTaxiAsync(driver);
        var journeyId = Guid.NewGuid();
        var plan = h.BuildPlan(("Nainital", Nainital, 2), ("Ranikhet", Ranikhet, 2));
        plan.Legs![1].Arrival = new TripTravel { FromName = "Nainital", JourneyId = journeyId, Cars = 1 };
        var tripId = await h.AddTripAsync(seeker, plan);
        var travelDate = h.Start.AddDays(2);
        await using (var s = h.NewScope())
        {
            s.Db.Set<SavedJourney>().Add(new SavedJourney
            {
                Id = journeyId, UserId = seeker, TripId = tripId, FromName = "Nainital", ToName = "Ranikhet",
                TravelDate = travelDate, People = 2, PlanKey = "p1", Cars = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                Legs =
                [
                    new SavedJourneyLeg
                    {
                        Id = Guid.NewGuid(), JourneyId = journeyId, LegIndex = 1, LegKey = "l1", FromName = "Nainital", ToName = "Ranikhet",
                        DriverTaxiId = taxi, ListedFare = 2500, UpdatedAt = DateTime.UtcNow,
                    },
                ],
            });
            await s.Db.SaveChangesAsync();
        }
        await AddRequestAsync(h, tripId, seeker, RequestStates.Collecting);

        try
        {
            var result = await h.TaxiBookAsync(tripId, seeker, new CreateTaxiBookingRequest
            {
                TaxiId = taxi, JourneyId = journeyId, JourneyLegIndex = 1, Days = [], PickupTime = "08:00", AgreedFare = 2500,
            });

            Assert.Equal(200, JourneyHarness.StatusOf(result));
            Assert.Single((await h.LoadAsync(tripId)).Enquiries);
        }
        finally
        {
            await using var cleanup = CreateContext();
            await cleanup.Set<SavedJourneyLeg>().Where(l => l.JourneyId == journeyId).ExecuteDeleteAsync();
            await cleanup.Set<SavedJourney>().Where(j => j.Id == journeyId).ExecuteDeleteAsync();
        }
    }
}

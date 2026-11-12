using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Tests.Data;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Integration.Journeys;

// T3: a traveller books a local taxi by phone ("I booked") and arranges his own stay; the trip is ready, not complete.
public class T3OwnCarTaxiBookedJourneyTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationFact]
    public async Task TaxiBooked_OwnStay_Ready_CompleteRefusedBeforeTheEnd()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var driver = await h.AddTaxiDriverAsync();
        var taxi = await h.AddTaxiAsync(driver);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));

        var booked = await h.TaxiBookAsync(tripId, seeker, new CreateTaxiBookingRequest
        {
            TaxiId = taxi, Days = [1, 2], AgreedFare = 6000, PickupTime = "08:30",
        });
        Assert.InRange(StatusOf(booked), 200, 201);

        var rows = await h.RowsAsync(tripId);
        var taxiRow = RowOf(rows, "taxi");
        Assert.Equal(TripRowStates.Booked, taxiRow.State);
        Assert.True(TripEasyRows.IsRowDone(taxiRow));
        var (trip, _, enquiries) = await h.LoadAsync(tripId);
        Assert.Equal(TripStatuses.Planned, trip.Status);
        Assert.Equal(6000, TripEasyRows.AgreedTotal(enquiries));
        Assert.Equal(TripBookingStates.Confirming, TripBookingStates.OfRows(rows));

        var marked = await h.UpdatePlanAsync(tripId, seeker, plan =>
        {
            plan.Legs![0].StayMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
            plan.Steps ??= new TripSteps();
            plan.Steps.GettingThere = new TripStep { Status = TripStepStatuses.Own };
            plan.Steps.ComingBack = new TripStep { Status = TripStepStatuses.Skipped };
        });
        Assert.Equal(200, StatusOf(marked));

        rows = await h.RowsAsync(tripId);
        Assert.Equal(TripRowStates.Own, RowOf(rows, "stay:1").State);
        Assert.Equal(TripRowStates.Booked, RowOf(rows, "taxi").State);
        Assert.Equal(TripBookingStates.Ready, TripBookingStates.OfRows(rows));

        var complete = await h.CompleteAsync(tripId, seeker);
        Assert.Equal(409, StatusOf(complete));
        Assert.Equal(TripStatuses.Planned, (await h.LoadAsync(tripId)).Trip.Status);
    }

    // The driver's "booked" and "withdrawn" pushes are outbox rows written in the booking transaction, not post-commit sends.
    [IntegrationFact]
    public async Task Booking_writes_the_drivers_push_to_the_outbox_in_the_same_transaction()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var driver = await h.AddTaxiDriverAsync();
        var taxi = await h.AddTaxiAsync(driver);
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));

        var booked = await h.TaxiBookAsync(tripId, seeker, new CreateTaxiBookingRequest
        {
            TaxiId = taxi, Days = [1, 2], AgreedFare = 6000, PickupTime = "08:30",
        });
        Assert.InRange(StatusOf(booked), 200, 201);

        var enquiry = Assert.Single((await h.LoadAsync(tripId)).Enquiries);
        await using var ctx = CreateContext();
        var keys = await ctx.OutboxMessages.AsNoTracking().Where(o => o.Key.StartsWith($"enq:{enquiry.Id:N}:")).Select(o => o.Key).ToListAsync();
        Assert.Single(keys, k => k.StartsWith($"enq:{enquiry.Id:N}:booked:"));
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(1, await ctx.NotificationEvents.AsNoTracking().CountAsync(n => n.TargetUserId == driver));
    }
}

using Microsoft.EntityFrameworkCore;
using RentNearBy.Tests.Data;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Integration.Journeys;

// T4: everything is arranged outside the app: marks only, nothing is asked, nobody is told.
public class T4OutsideAppMarksJourneyTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationFact]
    public async Task AllRowsOwnOrSkipped_ProgressFull_SummaryText_DeleteClosesNothing()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));

        var before = await h.ProgressAsync(tripId);
        Assert.Equal(4, before.Total);
        Assert.Equal(0, before.Done);

        var marked = await h.UpdatePlanAsync(tripId, seeker, plan =>
        {
            plan.Legs![0].StayMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
            plan.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
            plan.Steps ??= new TripSteps();
            plan.Steps.GettingThere = new TripStep { Status = TripStepStatuses.Own };
            plan.Steps.ComingBack = new TripStep { Status = TripStepStatuses.Skipped };
        });
        Assert.Equal(200, StatusOf(marked));

        var (trip, plan, enquiries) = await h.LoadAsync(tripId);
        Assert.Empty(enquiries);
        var rows = TripEasyRows.Build(trip, plan, enquiries, null);
        var progress = TripEasyRows.Progress(rows);
        Assert.Equal(3, progress.Total);
        Assert.Equal(progress.Total, progress.Done);
        Assert.Equal(100, progress.Percent);
        Assert.Equal(0, progress.InProgress);
        Assert.True(TripReminderRules.AllSettled(rows));
        Assert.Equal(TripRowStates.Skipped, RowOf(rows, "comingBack").State);
        Assert.Equal("Stay confirmed. Taxi confirmed.", TripReminderRules.StartsInSummary(trip, plan, enquiries));

        var events = h.Notifier.Events.Count;
        var posts = h.Chat.Posted.Count;
        var deleted = await h.DeleteTripAsync(tripId, seeker);
        Assert.Equal(200, StatusOf(deleted));
        Assert.Equal(events, h.Notifier.Events.Count);
        Assert.Equal(posts, h.Chat.Posted.Count);
        await using var ctx = CreateContext();
        Assert.False(await ctx.Trips.AnyAsync(t => t.Id == tripId));
    }
}

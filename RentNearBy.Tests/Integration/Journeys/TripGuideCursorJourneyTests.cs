using Microsoft.AspNetCore.Http;
using RentNearBy.Tests.Data;
using static RentNearBy.Tests.Integration.Journeys.JourneyHarness;

namespace RentNearBy.Tests.Integration.Journeys;

// The guide cursor is UI state: a save is never refused because of it (needs RENTNEARBY_TEST_DB).
public class TripGuideCursorJourneyTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationFact]
    public async Task Step_ahead_with_not_now_saves_and_both_are_stored()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));

        var saved = await h.UpdatePlanAsync(tripId, seeker, plan =>
            plan.Guide = new TripGuide { Step = "stay:1", NotNow = ["gettingThere"], Choices = new() { ["gettingThere"] = "find" } });
        Assert.Equal(200, StatusOf(saved));

        var (_, plan, _) = await h.LoadAsync(tripId);
        Assert.Equal("stay:1", plan.Guide!.Step);
        Assert.Equal(new[] { "gettingThere" }, plan.Guide.NotNow);
        Assert.Null(plan.Guide.Choices);

        Assert.Equal(200, StatusOf(await h.UpdatePlanAsync(tripId, seeker, p => p.Guide!.Step = "stay:1")));
        var (_, again, _) = await h.LoadAsync(tripId);
        Assert.Equal("stay:1", again.Guide!.Step);
        Assert.Equal(new[] { "gettingThere" }, again.Guide.NotNow);
    }

    [IntegrationFact]
    public async Task Unknown_step_is_dropped_and_the_save_succeeds()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));

        var saved = await h.UpdatePlanAsync(tripId, seeker, plan =>
            plan.Guide = new TripGuide { Step = "stay:2", NotNow = ["stay:2", "taxi"], Overview = true });
        Assert.Equal(200, StatusOf(saved));

        var (_, plan, _) = await h.LoadAsync(tripId);
        Assert.Null(plan.Guide!.Step);
        Assert.Equal(new[] { "taxi" }, plan.Guide.NotNow);
    }

    [IntegrationFact]
    public async Task Multi_city_steps_save_in_any_order()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Almora", 210000001, 2), ("Bhimtal", 210000002, 2)));

        foreach (var step in new[] { "stay:2", "between:2", "taxi", "stay:1" })
        {
            Assert.Equal(200, StatusOf(await h.UpdatePlanAsync(tripId, seeker, plan => plan.Guide = new TripGuide { Step = step })));
            Assert.Equal(step, (await h.LoadAsync(tripId)).Plan.Guide!.Step);
        }
    }

    [IntegrationFact]
    public async Task Create_with_a_step_ahead_is_accepted()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var plan = h.BuildPlan(("Nainital", 210000001, 2));
        plan.Guide = new TripGuide { Step = "stay:1", NotNow = ["gettingThere"] };

        await using var s = h.NewScope();
        var result = await RentNearBy.Api.Handlers.TripHandlers.Create(new RentNearBy.Core.DTOs.Requests.SaveTripRequest { Plan = plan },
            new RentNearBy.Api.Validators.SaveTripRequestValidator(), Principal(seeker), s.Uow, null!, s.Journeys, h.Loggers, h.RateLimit,
            new DefaultHttpContext(), s.Db);
        Assert.Equal(201, StatusOf(result));
    }

    [IntegrationFact]
    public async Task Own_and_skip_evidence_keeps_choices_and_unevidenced_ones_are_dropped()
    {
        await using var h = new JourneyHarness(Db);
        var seeker = await h.AddSeekerAsync();
        var tripId = await h.AddTripAsync(seeker, h.BuildPlan(("Nainital", 210000001, 2)));

        var saved = await h.UpdatePlanAsync(tripId, seeker, plan =>
        {
            plan.Steps = new TripSteps { GettingThere = new TripStep { Status = TripStepStatuses.Own } };
            plan.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped };
            plan.Guide = new TripGuide { Step = "stay:1", Choices = new() { ["gettingThere"] = "own", ["taxi"] = "skip", ["stay:1"] = "own" } };
        });
        Assert.Equal(200, StatusOf(saved));
        var choices = (await h.LoadAsync(tripId)).Plan.Guide!.Choices!;
        Assert.Equal("own", choices["gettingThere"]);
        Assert.Equal("skip", choices["taxi"]);
        Assert.False(choices.ContainsKey("stay:1"));
    }
}

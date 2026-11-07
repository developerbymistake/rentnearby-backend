using RentNearBy.Api.Handlers;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

// Close round D1: phone booking change base, silence rules, journey resolve for three roles, list predicates.
public class TripCloseRoundD1Tests
{
    private static readonly DateOnly Start = new(2026, 11, 1);

    private static TripPlan TwoCity(Guid? segmentJourney = null, Guid? there = null, Guid? back = null)
    {
        var plan = new TripPlan
        {
            Settings = new TripSettings { StartDate = Start },
            Legs =
            [
                new TripCityLeg { Index = 1, CityKey = 1, CityName = "Haridwar", Latitude = 29.9, Longitude = 78.1 },
                new TripCityLeg
                {
                    Index = 2, CityKey = 2, CityName = "Badrinath", Latitude = 30.7, Longitude = 79.5,
                    Arrival = segmentJourney == null ? null : new TripTravel { JourneyId = segmentJourney },
                },
            ],
            Steps = new TripSteps
            {
                GettingThere = new TripStep { JourneyId = there },
                ComingBack = new TripStep { JourneyId = back },
            },
        };
        for (var d = 1; d <= 4; d++) plan.Days.Add(new TripDay { Day = d, Leg = d <= 2 ? 1 : 2 });
        plan.Normalize();
        return plan;
    }

    [Fact]
    public void Resolve_finds_all_three_roles()
    {
        var there = Guid.NewGuid(); var back = Guid.NewGuid(); var seg = Guid.NewGuid();
        var plan = TwoCity(seg, there, back);
        Assert.Equal(TripStepRoles.GettingThere, TripJourneyLinks.Resolve(plan, there)!.Role);
        Assert.Equal(TripStepRoles.ComingBack, TripJourneyLinks.Resolve(plan, back)!.Role);
        var link = TripJourneyLinks.Resolve(plan, seg)!;
        Assert.Equal(TripStepRoles.Segment, link.Role);
        Assert.Equal(2, link.LegIndex);
        Assert.Null(TripJourneyLinks.Resolve(plan, Guid.NewGuid()));
    }

    [Fact]
    public void Resolve_expected_dates_per_role_and_a_moved_date_no_longer_matches()
    {
        var plan = TwoCity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var there = TripStepDates.Expected(TripStepRoles.GettingThere, plan, null);
        var back = TripStepDates.Expected(TripStepRoles.ComingBack, plan, null);
        var segment = TripStepDates.Expected(TripStepRoles.Segment, plan, 2);
        Assert.Equal(Start, there);
        Assert.Equal(plan.ComingBackDate, back);
        Assert.Equal(TripExecutionRules.NightDate(Start, plan.LegStart(2)), segment);
        plan.Settings!.StartDate = Start.AddDays(1);
        Assert.NotEqual(there, TripStepDates.Expected(TripStepRoles.GettingThere, plan, null));
        Assert.NotEqual(segment, TripStepDates.Expected(TripStepRoles.Segment, plan, 2));
    }

    [Fact]
    public void Active_and_past_predicates_are_exact_opposites_on_the_boundary()
    {
        var today = new DateOnly(2026, 11, 10);
        Trip T(string status, DateOnly? end) => new() { Status = status, EndDate = end };
        var rows = new[]
        {
            T(TripStatuses.Planned, null), T(TripStatuses.Planned, today.AddDays(-1)), T(TripStatuses.Planned, today.AddDays(-2)),
            T(TripStatuses.Completed, today.AddDays(5)), T(TripStatuses.Draft, today),
        };
        var past = TripListRules.PastPredicate(today).Compile();
        var active = TripListRules.ActivePredicate(today).Compile();
        foreach (var r in rows)
        {
            Assert.NotEqual(past(r), active(r));
            Assert.Equal(TripListRules.IsPast(r.Status, r.EndDate, today), past(r));
        }
        Assert.False(past(rows[1]));
        Assert.True(past(rows[2]));
        Assert.True(past(rows[3]));
    }

    [Theory]
    [InlineData(23, false)]
    [InlineData(24, true)]
    [InlineData(71, true)]
    [InlineData(72, false)]
    public void Booked_reminder_window(int hours, bool due)
    {
        var at = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
        var e = new TripEnquiry { Status = TripEnquiryStatuses.BookedByTraveller, BookedByTravellerAt = at };
        Assert.Equal(due, TripSilenceRules.BookedDue(e, at.AddHours(hours)));
    }

    [Fact]
    public void Booked_reminder_ignores_other_statuses()
    {
        var at = DateTime.UtcNow.AddHours(-30);
        Assert.False(TripSilenceRules.BookedDue(new TripEnquiry { Status = TripEnquiryStatuses.ConfirmedByBoth, BookedByTravellerAt = at }, DateTime.UtcNow));
        Assert.False(TripSilenceRules.BookedDue(new TripEnquiry { Status = TripEnquiryStatuses.BookedByTraveller }, DateTime.UtcNow));
    }

    [Theory]
    [InlineData(TripEnquiryStatuses.Asked, true)]
    [InlineData(TripEnquiryStatuses.FareSent, true)]
    [InlineData(TripEnquiryStatuses.BookedByTraveller, false)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth, false)]
    public void Paused_driver_closes_only_unanswered_local_leads(string status, bool closes)
    {
        var local = new TripEnquiry { Kind = TripEnquiryKinds.Taxi, TripId = Guid.NewGuid(), Day = null, Status = status };
        Assert.Equal(closes, TripSilenceRules.ClosesWhenPaused(local));
        var journeyCar = new TripEnquiry { Kind = TripEnquiryKinds.Taxi, TripId = Guid.NewGuid(), Day = 1, Status = status };
        Assert.False(TripSilenceRules.ClosesWhenPaused(journeyCar));
    }
}

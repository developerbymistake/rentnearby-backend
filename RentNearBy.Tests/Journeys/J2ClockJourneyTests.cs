using System.Text.Json;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Journeys;

// Story-shaped replay of the 3-day clock (UI_Template_resume/journeys/clock-reminders.md). Production has no TimeProvider; every run takes
// "now" or "today" as a parameter, so a day is a DateOnly and an instant is a UTC DateTime built from IST wall-clock values.
// What is real here: the planner, the push copy and keys, MorningDueUtc, ShouldRunMorning, EnquiryExpiry, OfferValidUntil, IsUrgent, WindowEnded.
// What is NOT proven here: the SQL that picks the rows each morning, the outbox insert, RabbitMQ and FCM (no database in this environment).
// The rows handed to the planner on each day follow the owner's rule (mornings of day 1, 2 and 3, final push the morning after the close);
// the count test below checks that rule against the real clock constants for every minute of the day.
// Expected values are literals from the owner's decisions (72 h window, 24 h offer, 08:00 IST, 3 pushes, cap 3).
public class J2ClockJourneyTests
{
    private static readonly Guid Kavita = Guid.Parse("c0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Host = Guid.Parse("c0000000-0000-0000-0000-00000000000b");
    private static readonly Guid TripId = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly DateOnly Mon = new(2026, 11, 16);

    private static DateTime Ist(DateOnly d, int h, int m = 0, int s = 0) => IstTime.AtUtc(d, h, m).AddSeconds(s);

    private static ReminderRow Row(string name, Guid? user = null, string? step = "stay:1", string kind = "Stay") => new()
    {
        UserId = user ?? Kavita, Id = Guid.NewGuid(), TripId = TripId, StepKey = step, Name = name, Kind = kind,
        At = Ist(Mon, 18),
    };

    // Morning run instant: the real run starts within a minute after 08:00 IST.
    private static DateTime RunAt(DateOnly day) => Ist(day, 8, 0, 30);

    [Fact]
    public void Owner_constants_are_what_the_spec_says()
    {
        Assert.Equal(72, TripExecutionRules.EnquiryHours);
        Assert.Equal(72, TripExecutionRules.ShortlistHours);
        Assert.Equal(24, TripExecutionRules.OfferHours);
        Assert.Equal(8, TripReminderRules.MorningHourIst);
        Assert.Equal(0, TripReminderRules.MorningMinuteIst);
        Assert.Equal(new DateTime(2026, 11, 16, 2, 30, 0, DateTimeKind.Utc), TripReminderRules.MorningDueUtc(Mon));
    }

    // Kavita: shortlists Monday 18:00 and never sends. Days 1, 2, 3 = one "waiting" push each, then the clock closes, then one final push.
    [Fact]
    public void Kavita_gets_three_morning_pushes_then_one_final_push_and_never_a_fourth_kind()
    {
        var row = Row("Hill View");
        var pushes = new List<(DateOnly Day, ReminderPlan Plan)>();
        var outbox = new HashSet<string>();

        foreach (var day in new[] { Mon.AddDays(1), Mon.AddDays(2), Mon.AddDays(3) })
        {
            var plan = Assert.Single(TripReminderPlanner.PlanTravellers(day, [], [], [row], []));
            Assert.Equal(ReminderCategory.Shortlist, plan.Category);
            Assert.True(outbox.Add(plan.Push.Key), "a day key repeated");
            pushes.Add((day, plan));
        }

        // Thursday 18:00: the 72 h clock closes the row; Friday 08:00 is the one final push.
        var friday = Mon.AddDays(4);
        var final = Assert.Single(TripReminderPlanner.PlanTravellers(friday, [row], [], [], []));
        Assert.Equal(ReminderCategory.Final, final.Category);
        Assert.True(outbox.Add(final.Push.Key));
        Assert.Equal("Your shortlist has ended", final.Push.Title);
        var args = JsonDocument.Parse(final.Push.ArgsJson!).RootElement;
        Assert.Equal("stay:1", args.GetProperty("step").GetString());
        Assert.True(args.GetProperty("reopen").GetBoolean());

        Assert.Equal(3, pushes.Count);
        Assert.All(pushes, p => Assert.Equal("Your shortlist is waiting", p.Plan.Push.Title));
        Assert.Equal(4, outbox.Count);
        // Closed rows are claimed by the final push in the same step (ids returned), so SQL stamps FinalPushedAt: nothing else to send on Saturday.
        Assert.Equal([row.Id], final.ShortlistIds);
        Assert.Empty(TripReminderPlanner.PlanTravellers(friday.AddDays(1), [], [], [], []));
    }

    // Every possible add time: the shortlist (72 h from adding) sees the 08:00 run on 2 or 3 mornings.
    // A morning counts when run time - minimum age > added and run time < close time (the SQL conditions AddedAt < runTime - 3 h and ClosesAt > now).
    // 3 mornings for most adds; an add from 05:01 to 08:00 is under 3 h old at the run and skips that morning, so it gets 2 and the final push comes on day 3.
    [Fact]
    public void Shortlist_sees_three_mornings_or_two_when_added_just_before_the_run()
    {
        var edges = new List<string>();
        for (var minute = 0; minute < 24 * 60; minute++)
        {
            var added = Ist(Mon, 0).AddMinutes(minute);
            var closes = added.AddHours(72);
            var count = 0;
            for (var d = -1; d <= 5; d++)
            {
                var run = TripReminderRules.MorningDueUtc(Mon.AddDays(d)).AddSeconds(30);
                if (added < run.AddHours(-TripReminderRules.TravellerMinAgeHours) && run < closes) count++;
            }
            var young = minute is >= 5 * 60 + 1 and <= 8 * 60; // the run starts 30 s after 08:00
            if (count != (young ? 2 : 3)) edges.Add($"{added:HH:mm} IST count {count}");
        }
        Assert.Empty(edges);
    }

    // C-G6: an add at 07:59 is not nudged at 08:00 the same morning; the first reminder is the next 08:00 and the cap still holds.
    [Fact]
    public void Item_added_at_07_59_first_gets_the_next_morning_and_the_cap_still_holds()
    {
        var added = Ist(Mon, 7, 59);
        Assert.Equal(Ist(Mon.AddDays(1), 8), TripReminderRules.FirstTravellerMorningUtc(added));
        var mornings = Enumerable.Range(0, 5).Select(d => RunAt(Mon.AddDays(d)))
            .Where(r => added < r.AddHours(-TripReminderRules.TravellerMinAgeHours) && r < added.AddHours(72)).ToList();
        Assert.Equal(2, mornings.Count);
        Assert.Equal(Mon.AddDays(1), DateOnly.FromDateTime(mornings[0] + IstTime.Offset));
    }

    // Vikram sends an enquiry Tuesday 10:00 and nobody answers: traveller "No reply yet" on Wed, Thu, Fri; host "waiting" on the same mornings; Saturday final.
    [Fact]
    public void Vikram_and_the_silent_host_are_reminded_on_days_one_to_three_then_one_final_push()
    {
        var traveller = Row("Hill View");
        var hostRow = Row("Vikram", Host);
        hostRow.Id = traveller.Id;
        var keys = new HashSet<string>();
        foreach (var d in new[] { 1, 2, 3 })
        {
            var day = Mon.AddDays(d);
            var t = Assert.Single(TripReminderPlanner.PlanTravellers(day, [], [], [], [traveller]));
            Assert.Equal(ReminderCategory.NoReply, t.Category);
            Assert.Equal("No reply yet", t.Push.Title);
            var h = Assert.Single(TripReminderPlanner.PlanHosts(day, [hostRow]));
            Assert.Equal("1 traveller waiting", h.Push.Title);
            Assert.True(keys.Add(t.Push.Key));
            Assert.True(keys.Add(h.Push.Key));
        }
        Assert.Equal(6, keys.Count);

        var final = Assert.Single(TripReminderPlanner.PlanTravellers(Mon.AddDays(4), [], [traveller], [], []));
        Assert.Equal(ReminderCategory.Final, final.Category);
        Assert.Equal([traveller.Id], final.EnquiryIds);
        // The host list does not get a fourth nudge: the enquiry has left the host rows (Expired), so the host planner has nothing.
        Assert.Empty(TripReminderPlanner.PlanHosts(Mon.AddDays(4), []));
    }

    // Phone-booking nudge: every shortlist and no-reply push carries the "Mark it as booked" text and nudge=true so the app can offer
    // Mark as booked and Choose new options; the final push is a reopen, not a nudge.
    [Fact]
    public void Nudge_pushes_offer_mark_as_booked_and_the_final_push_offers_new_options()
    {
        var s = Assert.Single(TripReminderPlanner.PlanTravellers(Mon.AddDays(1), [], [], [Row("Hill View")], []));
        var o = Assert.Single(TripReminderPlanner.PlanTravellers(Mon.AddDays(1), [], [], [], [Row("Hill View")]));
        foreach (var p in new[] { s, o })
        {
            Assert.Contains("Mark it as booked", p.Push.Body);
            var a = JsonDocument.Parse(p.Push.ArgsJson!).RootElement;
            Assert.True(a.GetProperty("nudge").GetBoolean());
            Assert.False(a.GetProperty("reopen").GetBoolean());
            Assert.Equal(UserNotificationRoutes.TripPlan, p.Push.Route);
        }
        var f = Assert.Single(TripReminderPlanner.PlanTravellers(Mon.AddDays(4), [Row("Hill View")], [], [], []));
        var fa = JsonDocument.Parse(f.Push.ArgsJson!).RootElement;
        Assert.False(fa.GetProperty("nudge").GetBoolean());
        Assert.True(fa.GetProperty("reopen").GetBoolean());
    }

    // The nudge stops once the traveller marks the step booked: the settled step leaves the row sets, so the planner returns nothing.
    // (The SQL that removes the rows is pinned by text in B3ReminderGapTests; here the planner side of the stop.)
    [Fact]
    public void After_mark_booked_no_row_reaches_the_planner_and_no_push_is_made()
        => Assert.Empty(TripReminderPlanner.PlanTravellers(Mon.AddDays(2), [], [], [], []));

    // Restart: the run crashes after the outbox insert and runs again the same morning. Same people give the same keys, so ON CONFLICT DO NOTHING
    // (modelled here as a set) lets nobody through twice; the next day has fresh keys.
    [Fact]
    public void Restart_the_same_morning_makes_the_same_keys_and_the_next_day_new_ones()
    {
        var rows = new[] { Row("A"), Row("B", Guid.Parse("c0000000-0000-0000-0000-0000000000c1")) };
        var day = Mon.AddDays(1);
        var first = TripReminderPlanner.PlanTravellers(day, [], [], rows, []);
        var again = TripReminderPlanner.PlanTravellers(day, [], [], rows, []);
        Assert.Equal(first.Select(p => p.Push.Key).Order(), again.Select(p => p.Push.Key).Order());
        var outbox = new HashSet<string>();
        Assert.All(first, p => Assert.True(outbox.Add(p.Push.Key)));
        Assert.All(again, p => Assert.False(outbox.Add(p.Push.Key)));
        var next = TripReminderPlanner.PlanTravellers(day.AddDays(1), [], [], rows, []);
        Assert.All(next, p => Assert.True(outbox.Add(p.Push.Key)));
        Assert.Equal("rem:t:c000000000000000000000000000000a:20261117", TripReminderRules.TravellerKey(Kavita, day));
    }

    [Fact]
    public void One_push_a_day_per_person_with_final_first_then_shortlist_then_no_reply()
    {
        var day = Mon.AddDays(4);
        var plans = TripReminderPlanner.PlanTravellers(day, [Row("F")], [], [Row("S")], [Row("O")]);
        Assert.Equal(ReminderCategory.Final, Assert.Single(plans).Category);
    }

    // Server down at 08:00: catch-up until 12:00 IST, never in the afternoon; a missed day is flagged from 12:00.
    [Fact]
    public void Restart_catch_up_window_and_missed_day()
    {
        var day = Mon.AddDays(1);
        Assert.False(TripReminderRules.ShouldRunMorning(Ist(day, 7, 59, 59), false));
        Assert.True(TripReminderRules.ShouldRunMorning(Ist(day, 8), false));
        Assert.True(TripReminderRules.ShouldRunMorning(Ist(day, 11, 59, 59), false));
        Assert.False(TripReminderRules.ShouldRunMorning(Ist(day, 12), false));
        Assert.False(TripReminderRules.ShouldRunMorning(Ist(day, 7), true));
        Assert.False(TripReminderRules.RunMissed(Ist(day, 11, 59), false));
        Assert.True(TripReminderRules.RunMissed(Ist(day, 12), false));
        Assert.False(TripReminderRules.RunMissed(Ist(day, 12), true));
    }

    // Enquiry window: 72 h from the tap when the trip is far; 12 h before the start day when it is near; never less than 2 h.
    [Fact]
    public void Enquiry_window_boundaries()
    {
        var now = Ist(Mon, 10);
        var far = Mon.AddDays(30);
        Assert.Equal((now.AddHours(72), false), TripExecutionRules.EnquiryExpiry(far, far, now));

        // Trip starts in exactly 3 days at 00:00 IST + 12 h => reference - 12 h is 60 h away: shorter than 72 h wins.
        var start = Mon.AddDays(3);
        var (e1, short1) = TripExecutionRules.EnquiryExpiry(start, start, now);
        Assert.Equal(Ist(start, 0).AddHours(-12), e1);
        Assert.False(short1);

        // Start tomorrow 00:00 IST: reference - 12 h is already past the 2 h floor: floor applies, flagged very short notice.
        var tomorrow = Mon.AddDays(1);
        var (e2, short2) = TripExecutionRules.EnquiryExpiry(tomorrow, tomorrow, Ist(Mon, 20));
        Assert.Equal(Ist(Mon, 20).AddHours(2), e2);
        Assert.True(short2);
    }

    [Fact]
    public void Window_ends_at_the_exact_instant_and_only_for_an_asked_enquiry()
    {
        var expires = Ist(Mon, 12);
        Assert.False(TripBulkRules.WindowEnded("Asked", expires, expires.AddSeconds(-1)));
        Assert.True(TripBulkRules.WindowEnded("Asked", expires, expires));
        Assert.True(TripBulkRules.WindowEnded("Asked", expires, expires.AddSeconds(1)));
        Assert.False(TripBulkRules.WindowEnded("Offered", expires, expires.AddHours(5)));
    }

    // Offer: 24 h when the trip is 72 h or more away; 2 h when it starts inside 72 h (the 72 h edge itself is not urgent); cut at check-in midnight.
    [Fact]
    public void Offer_validity_boundaries()
    {
        var now = Ist(Mon, 10);
        var far = Mon.AddDays(30);
        Assert.Equal(now.AddHours(24), TripExecutionRules.OfferValidUntil(far, far, now));
                // start exactly 72 h after now: IST midnight of start minus now = 72 h => not urgent (strict less than).
        var startExact = Mon.AddDays(3);
        Assert.False(TripExecutionRules.IsUrgent(startExact, Ist(Mon, 0)));
        Assert.True(TripExecutionRules.IsUrgent(startExact, Ist(Mon, 0).AddSeconds(1)));
        Assert.Equal(Ist(Mon, 10).AddHours(2), TripExecutionRules.OfferValidUntil(Mon.AddDays(2), Mon.AddDays(2), Ist(Mon, 10)));
        // Offer can never run below 1 h.
        var late = TripExecutionRules.OfferValidUntil(Mon.AddDays(1), Mon.AddDays(1), Ist(Mon, 23, 30));
        Assert.Equal(Ist(Mon, 23, 30).AddHours(1), late);
    }

    [Fact]
    public void Every_cap_is_three_and_the_pick_message_is_the_spec_text()
    {
        Assert.Equal(3, TripExecutionRules.MaxRecipientsPerEnquiry);
        Assert.Equal(3, TripExecutionRules.MaxShortlistPerStep);
        Assert.True(TripBulkRules.StepHasRoom(0, 2));
        Assert.False(TripBulkRules.StepHasRoom(1, 2));
        Assert.False(TripBulkRules.StepHasRoom(3, 0));
        Assert.Equal("Pick 1 to 3.", TripExecutionRules.PickMessage);
        Assert.Equal("You already have 3 open enquiries here. Wait for a reply or withdraw one.", TripBulkRules.StepFullMessage);
    }
}

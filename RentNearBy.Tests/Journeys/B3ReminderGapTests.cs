using System.Reflection;
using System.Text.Json;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Journeys;

// B3 reminder gaps. The raw SQL cannot run here without PostgreSQL, so the predicates are pinned by text and the planner is run for real;
// TripReminderIntegrationTests holds the database versions (skipped without RENTNEARBY_TEST_DB).
public class B3ReminderGapTests
{
    private static readonly DateOnly Today = new(2026, 11, 12);
    private static readonly DateTime Now = new(2026, 11, 12, 1, 30, 0, DateTimeKind.Utc);

    private static string Sql(string name)
    {
        var f = typeof(TripReminderRunner).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)f.GetValue(null)!;
    }

    private static ReminderRow Row(Guid user, string? step = "stay:1", string name = "Hill View", bool chatted = false, bool lapsed = false) => new()
    {
        UserId = user, Id = Guid.NewGuid(), TripId = Guid.NewGuid(), StepKey = step, Name = name, At = Now.AddDays(-1), Kind = "Stay",
        HostChatted = chatted, OfferLapsed = lapsed,
    };

    private static readonly Guid U = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    // Real life: the host answered in chat at 9 pm; the 8 am push to the traveller must not say "No reply yet".
    [Fact]
    public void Host_who_chatted_is_not_told_as_no_reply()
    {
        var plan = Assert.Single(TripReminderPlanner.PlanTravellers(Today, [], [], [], [Row(U, chatted: true)]));
        Assert.DoesNotContain("No reply", plan.Push.Title);
        Assert.DoesNotContain("not replied", plan.Push.Body);
        Assert.Contains("chat", plan.Push.Body);
    }

    [Fact]
    public void Silent_hosts_still_get_the_no_reply_line_and_chatted_ones_are_not_counted()
    {
        var plan = Assert.Single(TripReminderPlanner.PlanTravellers(Today, [], [], [],
            [Row(U, name: "Silent Stay"), Row(U, name: "Chatty Stay", chatted: true)]));
        Assert.Equal("No reply yet", plan.Push.Title);
        Assert.Contains("Silent Stay", plan.Push.Body);
        Assert.DoesNotContain("more", plan.Push.Body);
    }

    // Real life: Meena's offer from Green Stay lapsed after 24 h and the enquiry then ran out.
    [Fact]
    public void Final_push_for_a_lapsed_offer_says_the_offer_ended()
    {
        var plan = Assert.Single(TripReminderPlanner.PlanTravellers(Today, [], [Row(U, name: "Green Stay", lapsed: true)], [], []));
        Assert.Equal("The offer from Green Stay ended. Choose again to complete your trip.", plan.Push.Body);
        Assert.Equal(ReminderCategory.Final, plan.Category);
    }

    // Real life: a step-less enquiry (old client, no StepKey) ended with no reply. The push opens the trip plan, not a step.
    [Fact]
    public void Final_push_for_a_step_less_enquiry_opens_the_trip_plan_with_a_generic_line()
    {
        var row = Row(U, step: null, name: "Lake Taxi");
        var plan = Assert.Single(TripReminderPlanner.PlanTravellers(Today, [], [row], [], []));
        Assert.Equal("Your enquiry has ended", plan.Push.Title);
        Assert.Equal(UserNotificationRoutes.TripPlan, plan.Push.Route);
        using var args = JsonDocument.Parse(plan.Push.ArgsJson!);
        Assert.False(args.RootElement.TryGetProperty("step", out _));
        Assert.Equal(row.TripId!.Value.ToString(), args.RootElement.GetProperty("tripId").GetString());
        Assert.Equal([row.Id], plan.EnquiryIds);
    }

    [Fact]
    public void A_step_row_wins_over_a_step_less_row_for_the_final_push()
    {
        var plan = Assert.Single(TripReminderPlanner.PlanTravellers(Today, [Row(U, step: "stay:2")], [Row(U, step: null)], [], []));
        Assert.Equal("Your shortlist has ended", plan.Push.Title);
        using var args = JsonDocument.Parse(plan.Push.ArgsJson!);
        Assert.Equal("stay:2", args.RootElement.GetProperty("step").GetString());
        Assert.Equal(2, plan.ShortlistIds.Count + plan.EnquiryIds.Count);
    }

    [Fact]
    public void Host_minimum_age_is_twelve_hours_and_traveller_minimum_age_is_three()
    {
        Assert.Equal(12, TripReminderRules.HostMinAgeHours);
        Assert.Equal(3, TripReminderRules.TravellerMinAgeHours);
        Assert.Contains($"{{2}} - interval '{TripReminderRules.HostMinAgeHours} hours'", Sql("EnquiryHostWaiting"));
        Assert.DoesNotContain("interval '12 hours'", Sql("EnquiryOut"));
        Assert.Contains($"\"CreatedAt\" < {{2}} - interval '{TripReminderRules.TravellerMinAgeHours} hours'", Sql("EnquiryOut"));
        Assert.Contains($"\"AddedAt\" < {{2}} - interval '{TripReminderRules.TravellerMinAgeHours} hours'", Sql("ShortlistActive"));
    }

    // Mute silences the traveller only: the host queries never read Trips.MutedAt.
    [Fact]
    public void Mute_does_not_stop_host_reminders()
    {
        Assert.DoesNotContain("MutedAt", Sql("HostTripOk"));
        Assert.DoesNotContain("MutedAt", Sql("HostFrom"));
        Assert.DoesNotContain("MutedAt", Sql("EnquiryHostWaiting"));
        Assert.Contains("\"MutedAt\" IS NULL", Sql("TripOk"));
    }

    [Fact]
    public void Host_query_skips_owner_chat_settled_steps_and_lapsed_offers()
    {
        var sql = Sql("EnquiryHostWaiting");
        Assert.Contains("NOT EXISTS (SELECT 1 FROM \"TripEnquiries\" c", sql);
        Assert.Contains("'ConfirmedByBoth','BookedByTraveller'", sql);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM \"TripOffers\"", sql);
        Assert.Contains("'Expired'", sql);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM \"Messages\" m", sql);
        Assert.Contains("m.\"Type\" <> 'system'", sql);
        Assert.Contains("m.\"SenderId\" = e.\"OwnerUserId\"", sql);
        Assert.Contains("m.\"CreatedAt\" > e.\"CreatedAt\"", sql);
    }

    [Fact]
    public void Traveller_no_reply_query_skips_lapsed_offers()
        => Assert.Contains("NOT EXISTS (SELECT 1 FROM \"TripOffers\"", Sql("EnquiryOut"));

    [Fact]
    public void Host_queries_use_a_left_join_so_trip_less_enquiries_are_reminded()
    {
        Assert.Contains("LEFT JOIN \"Trips\"", Sql("HostFrom"));
        Assert.Contains("t.\"Id\" IS NULL", Sql("HostTripOk"));
    }

    [Theory]
    [InlineData("ShortlistActive")]
    [InlineData("ShortlistFinal")]
    public void Shortlist_filters_skip_steps_with_an_open_enquiry(string name)
    {
        var sql = Sql(name);
        Assert.Contains("'Asked','Offered','Countered','FareSent'", sql);
        Assert.Contains("'ConfirmedByBoth','BookedByTraveller'", sql);
    }

    [Theory]
    [InlineData("ShortlistFinal")]
    [InlineData("EnquiryFinal")]
    public void Final_predicates_look_back_two_days_only(string name)
    {
        Assert.Equal(2, TripReminderRules.FinalWindowDays);
        Assert.Contains("{1} - interval '2 days'", Sql(name));
    }

    [Fact]
    public void Enquiry_final_includes_step_less_rows_and_keeps_the_full_clock_rule()
    {
        var sql = Sql("EnquiryFinal");
        Assert.DoesNotContain("\"StepKey\" IS NOT NULL", sql);
        Assert.Contains($"interval '{TripExecutionRules.EnquiryHours} hours'", sql);
    }

    // Real life: the 08:00 run never finished (crash, lock held by a dead instance). At noon ops must hear about it.
    [Theory]
    [InlineData(5, 29, false)]   // 10:59 IST, still catching up
    [InlineData(6, 30, true)]    // 12:00 IST
    [InlineData(10, 0, true)]
    public void Missed_run_is_flagged_from_noon_ist_when_nothing_finished(int h, int m, bool missed)
        => Assert.Equal(missed, TripReminderRules.RunMissed(new DateTime(2026, 11, 12, h, m, 0, DateTimeKind.Utc), todayFinished: false));

    [Fact]
    public void A_finished_run_is_never_flagged()
        => Assert.False(TripReminderRules.RunMissed(new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc), todayFinished: true));

    [Fact]
    public void Sent_shortlist_rows_are_kept_for_thirty_days()
        => Assert.Equal(30, TripReminderRules.SentShortlistRetentionDays);

    [Fact]
    public void Daily_stamp_statement_exists_and_purge_is_gone()
    {
        var timer = typeof(TripTimerService);
        Assert.NotNull(timer.GetMethod("DailyFinalStampPassAsync", BindingFlags.NonPublic | BindingFlags.Static));
        Assert.Null(timer.GetMethod("PurgePassAsync", BindingFlags.NonPublic | BindingFlags.Static));
    }
}

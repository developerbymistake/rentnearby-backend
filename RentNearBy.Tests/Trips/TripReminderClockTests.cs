using System.Text.Json;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

public class TripReminderClockTests
{
    private static readonly DateOnly Today = new(2026, 11, 12);
    private static readonly Guid User = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Trip = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private static ReminderRow Row(string name, string? step = "stay:1", Guid? user = null, int minute = 0, string kind = "Stay", DateOnly? on = null) => new()
    {
        UserId = user ?? User, Id = Guid.NewGuid(), TripId = Trip, StepKey = step, Name = name,
        At = new DateTime(2026, 11, 9, 10, minute, 0, DateTimeKind.Utc), On = on, Kind = kind,
    };

    private static IReadOnlyList<ReminderPlan> Plan(
        ReminderRow[]? finalS = null, ReminderRow[]? finalE = null, ReminderRow[]? active = null, ReminderRow[]? outs = null)
        => TripReminderPlanner.PlanTravellers(Today, finalS ?? [], finalE ?? [], active ?? [], outs ?? []);

    private static IReadOnlyList<ReminderPlan> Hosts(params ReminderRow[] rows) => TripReminderPlanner.PlanHosts(Today, rows);

    [Fact]
    public void Shortlist_not_sent_asks_to_send_and_to_mark_booked()
    {
        var plan = Assert.Single(Plan(active: [Row("Hill View")]));
        Assert.Equal(ReminderCategory.Shortlist, plan.Category);
        Assert.Equal("Your shortlist is waiting", plan.Push.Title);
        Assert.Equal("Send your enquiry to Hill View today. Did you already book Hill View? Mark it as booked.", plan.Push.Body);
        Assert.Equal(UserNotificationRoutes.TripPlan, plan.Push.Route);
        var args = JsonDocument.Parse(plan.Push.ArgsJson!).RootElement;
        Assert.Equal("stay:1", args.GetProperty("step").GetString());
        Assert.True(args.GetProperty("nudge").GetBoolean());
        Assert.False(args.GetProperty("reopen").GetBoolean());
        Assert.Equal("Hill View", args.GetProperty("name").GetString());
    }

    [Fact]
    public void Several_shortlisted_items_group_into_one_push()
    {
        var plan = Assert.Single(Plan(active: [Row("A", minute: 1), Row("B", minute: 2), Row("C", minute: 3)]));
        Assert.Equal("Send your enquiry to A and 2 more today. Did you already book? Mark it as booked.", plan.Push.Body);
        Assert.Equal(3, plan.ShortlistIds.Count);
    }

    [Fact]
    public void Enquiry_out_without_reply_has_its_own_copy_and_claims_nothing()
    {
        var plan = Assert.Single(Plan(outs: [Row("Hill View")]));
        Assert.Equal(ReminderCategory.NoReply, plan.Category);
        Assert.Equal("No reply yet", plan.Push.Title);
        Assert.Equal("Hill View has not replied. Did you already book Hill View? Mark it as booked.", plan.Push.Body);
        Assert.Empty(plan.ShortlistIds);
        Assert.Empty(plan.EnquiryIds);
    }

    [Fact]
    public void Closed_step_gets_the_single_final_push_that_reopens_the_step()
    {
        var plan = Assert.Single(Plan(finalS: [Row("Hill View", "stay:2")]));
        Assert.Equal(ReminderCategory.Final, plan.Category);
        Assert.Equal("Your shortlist has ended", plan.Push.Title);
        Assert.Equal("Your shortlist has ended without a reply. Choose new options to complete your trip.", plan.Push.Body);
        var args = JsonDocument.Parse(plan.Push.ArgsJson!).RootElement;
        Assert.Equal("stay:2", args.GetProperty("step").GetString());
        Assert.True(args.GetProperty("reopen").GetBoolean());
        Assert.False(args.GetProperty("nudge").GetBoolean());
    }

    [Fact]
    public void Expired_enquiry_final_claims_the_enquiry_row()
    {
        var e = Row("Hill View");
        var plan = Assert.Single(Plan(finalE: [e]));
        Assert.Equal(ReminderCategory.Final, plan.Category);
        Assert.Equal([e.Id], plan.EnquiryIds);
    }

    [Fact]
    public void One_push_per_person_per_day_with_final_first_then_shortlist_then_no_reply()
    {
        var first = Assert.Single(Plan(finalS: [Row("F")], active: [Row("S")], outs: [Row("O")]));
        Assert.Equal(ReminderCategory.Final, first.Category);
        // The shortlist row was not claimed by the final push, so it stays due for the next morning.
        Assert.Single(first.ShortlistIds);

        var second = Assert.Single(Plan(active: [Row("S")], outs: [Row("O")]));
        Assert.Equal(ReminderCategory.Shortlist, second.Category);
    }

    [Fact]
    public void People_are_separate_pushes_with_the_day_key()
    {
        var other = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
        var plans = Plan(active: [Row("A"), Row("B", user: other)]);
        Assert.Equal(2, plans.Count);
        Assert.Equal(TripReminderRules.TravellerKey(User, Today), plans.Single(p => p.UserId == User).Push.Key);
        Assert.Equal(TripReminderRules.TravellerKey(other, Today), plans.Single(p => p.UserId == other).Push.Key);
        Assert.All(plans, p => Assert.Null(p.Push.NotificationId));
    }

    [Fact]
    public void Nothing_due_means_no_push()
        => Assert.Empty(Plan());

    [Fact]
    public void Host_with_one_waiting_traveller_gets_the_named_copy()
    {
        var e = Row("Hill View", kind: "Stay", on: new DateOnly(2026, 12, 3));
        e.Name = "Asha";
        var plan = Assert.Single(Hosts(e));
        Assert.Equal(ReminderCategory.Host, plan.Category);
        Assert.Equal("1 traveller waiting", plan.Push.Title);
        Assert.Equal("Please reply to Asha's enquiry for 3 Dec.", plan.Push.Body);
        Assert.Equal(UserNotificationRoutes.StayEnquiries, plan.Push.Route);
        Assert.Equal([e.Id], plan.EnquiryIds);
        Assert.Equal(TripReminderRules.HostKey(User, Today), plan.Push.Key);
    }

    [Fact]
    public void Host_with_several_waiting_gets_one_grouped_push()
    {
        var plan = Assert.Single(Hosts(Row("a"), Row("b"), Row("c")));
        Assert.Equal("3 travellers waiting", plan.Push.Title);
        Assert.Equal("3 travellers are waiting for your reply.", plan.Push.Body);
        Assert.Equal(3, plan.EnquiryIds.Count);
    }

    [Fact]
    public void Taxi_host_opens_the_driver_leads()
    {
        var plan = Assert.Single(Hosts(Row("Dzire", kind: "Taxi")));
        Assert.Equal(UserNotificationRoutes.TripLeads, plan.Push.Route);
    }

    [Fact]
    public void Long_names_are_cut_and_never_break_the_copy()
    {
        var plan = Assert.Single(Plan(active: [Row(new string('x', 200))]));
        Assert.Contains("…", plan.Push.Body);
        Assert.True(plan.Push.Body.Length < 200);
    }

    [Fact]
    public void Both_roles_get_one_push_each_with_different_keys()
    {
        var traveller = Assert.Single(Plan(active: [Row("A")]));
        var host = Assert.Single(Hosts(Row("A")));
        Assert.NotEqual(traveller.Push.Key, host.Push.Key);
    }
}

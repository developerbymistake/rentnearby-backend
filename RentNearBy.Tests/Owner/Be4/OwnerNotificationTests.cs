using System.Text.Json;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be4;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Be4;

// Contract 5.3 push copy (exact strings, singular / plural, 120-character cut, never an amount) and the outbox payload shape.
public class OwnerPushCopyTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Listing = Guid.NewGuid();
    private static readonly Guid Festival = Guid.NewGuid();
    private static readonly DateOnly Today = Be4Facts.Today;

    [Fact]
    public void Stay_festival_reads_as_the_doc()
    {
        var p = StayOwnerPushCopy.Festival(User, Festival, "Diwali", new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 12), 21);
        Assert.Equal("Diwali is in 3 weeks", p.Title);
        Assert.Equal("Set your price for 6 to 12 Nov?", p.Body);
        Assert.Equal(NotificationTypes.FestivalReminder, p.Type);
        Assert.Equal($"festival:{Festival}:{User}", p.Key);
        Assert.Equal(UserNotificationRoutes.StaySpecialPrices, p.Route);
        Assert.Contains("\"festivalId\"", p.ArgsJson);
        Assert.Contains("\"dateFrom\":\"2026-11-06\"", p.ArgsJson);
    }

    [Fact]
    public void Festival_range_across_months_names_both_months()
        => Assert.Equal("Set your price for 30 Oct to 5 Nov?",
            StayOwnerPushCopy.Festival(User, Festival, "Diwali", new DateOnly(2026, 10, 30), new DateOnly(2026, 11, 5), 20).Body);

    [Fact]
    public void Taxi_festival_reads_as_the_doc()
    {
        var p = TaxiDriverPushCopy.Festival(User, Festival, "Diwali", new DateOnly(2026, 11, 6));
        Assert.Equal("Diwali week starts 6 Nov", p.Title);
        Assert.Equal("Go online and check your fares.", p.Body);
        Assert.Equal(UserNotificationRoutes.TaxiRoutesAndFares, p.Route);
        Assert.Contains("\"section\":\"routes\"", p.ArgsJson);
        Assert.Equal("Holi week", TaxiDriverPushCopy.WeekName("Holi week"));
    }

    [Fact]
    public void Plans_started_copy_counts_what_is_hidden()
    {
        Assert.Equal("4 photos and 2 room types are inactive on Free. Nothing is deleted.", StayOwnerPushCopy.PlansStarted(User, 4, 2, Today).Body);
        Assert.Equal("1 photo is inactive on Free. Nothing is deleted.", StayOwnerPushCopy.PlansStarted(User, 1, 0, Today).Body);
        Assert.Equal("3 routes are inactive on Free. Nothing is deleted.", TaxiDriverPushCopy.PlansStarted(User, 3, Today).Body);
        Assert.Equal("1 route is inactive on Free. Nothing is deleted.", TaxiDriverPushCopy.PlansStarted(User, 1, Today).Body);
        Assert.Equal("Plans have started", TaxiDriverPushCopy.PlansStarted(User, 1, Today).Title);
        Assert.Equal($"planlimits:{User}:20261121", TaxiDriverPushCopy.PlansStarted(User, 1, Today).Key);
    }

    [Fact]
    public void Stay_edit_copy()
    {
        var at = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Name updated to Lake View", StayOwnerPushCopy.EditApproved(User, Listing, at, "Lake View").Body);
        Assert.Equal("Your stay details are updated", StayOwnerPushCopy.EditApproved(User, Listing, at, null).Body);
        Assert.Equal("Your change is live", StayOwnerPushCopy.EditApproved(User, Listing, at, null).Title);
        var rejected = StayOwnerPushCopy.EditRejected(User, Listing, at, "The photo of the licence is not clear");
        Assert.Equal("Change not approved", rejected.Title);
        Assert.Equal($"stayedit:{Listing}:{at.Ticks}", rejected.Key);
    }

    [Fact]
    public void Morning_check_ins_singular_and_plural_with_detail()
    {
        var one = StayOwnerPushCopy.MorningCheckIns(User, Listing, Today, [new OwnerReminderLine(CalendarEntryKinds.Auto, "Neha Joshi", "Family Room · 2 guests", null)]);
        Assert.Equal("1 check-in today", one.Title);
        Assert.Equal("Neha Joshi (Family Room · 2 guests)", one.Body);
        Assert.Equal($"ownermorning:Stay:{Listing}:20261121", one.Key);
        Assert.Equal(UserNotificationRoutes.StayCalendar, one.Route);
        Assert.Equal(new DateTime(2026, 11, 21, 6, 30, 0), one.ExpiresAtUtc);
        var two = StayOwnerPushCopy.MorningCheckIns(User, Listing, Today,
            [new OwnerReminderLine(CalendarEntryKinds.Auto, "Neha", null, null), new OwnerReminderLine(CalendarEntryKinds.Manual, "Phone booking", null, null)]);
        Assert.Equal("2 check-ins today", two.Title);
        Assert.Equal("Neha, Phone booking", two.Body);
    }

    [Fact]
    public void Morning_trips_all_day_or_time()
    {
        var p = TaxiDriverPushCopy.MorningTrips(User, Listing, Today,
        [
            new OwnerReminderLine(CalendarEntryKinds.Manual, "Airport drop", null, new TimeOnly(9, 0)),
            new OwnerReminderLine(CalendarEntryKinds.Auto, "Seema", "4 seats", null, "Nainital"),
            new OwnerReminderLine(CalendarEntryKinds.Manual, "School run", null, new TimeOnly(15, 30)),
        ]);
        Assert.Equal("3 trips today", p.Title);
        // FB-48: an app booking shows its pickup place, never the traveller's name.
        Assert.Equal("Nainital pickup (all day), Airport drop 9:00 am, School run 3:30 pm", p.Body);
        Assert.Equal("Trip (all day)", TaxiDriverPushCopy.MorningTrips(User, Listing, Today, [new OwnerReminderLine(CalendarEntryKinds.Auto, "Seema", null, null)]).Body);
        Assert.Equal(UserNotificationRoutes.TaxiBookings, p.Route);
        Assert.Equal("1 trip today", TaxiDriverPushCopy.MorningTrips(User, Listing, Today, [new OwnerReminderLine("Manual", "A", null, null)]).Title);
    }

    [Fact]
    public void Bodies_are_cut_at_120_characters()
    {
        var reason = new string('x', 300);
        var body = StayOwnerPushCopy.EditRejected(User, Listing, DateTime.UtcNow, reason).Body;
        Assert.Equal(120, body.Length);
        Assert.EndsWith("...", body);
        var lines = Enumerable.Range(0, 30).Select(i => new OwnerReminderLine(CalendarEntryKinds.Manual, $"Guest number {i}", null, null)).ToList();
        Assert.True(StayOwnerPushCopy.MorningCheckIns(User, Listing, Today, lines).Body.Length <= OwnerPushText.BodyMax);
    }

    [Fact]
    public void Plans_starting_copy_uses_free_values_and_the_trial_only_when_one_exists()
    {
        var stay = StayOwnerPushCopy.PlansStarting(User, new DateOnly(2027, 2, 1), 2, 2, 7);
        Assert.Equal("Plans start on 1 Feb", stay.Title);
        Assert.Equal("Free keeps 2 photos and 2 room types. Try Plus free for 7 days.", stay.Body);
        Assert.Equal("Free keeps 1 route. Try Plus free for 7 days.", TaxiDriverPushCopy.PlansStarting(User, new DateOnly(2027, 2, 1), 1, 7).Body);
        Assert.Equal("Free keeps 1 route.", TaxiDriverPushCopy.PlansStarting(User, new DateOnly(2027, 2, 1), 1, 0).Body);
        Assert.Equal($"plansstarting:{User}:20270201", stay.Key);
        Assert.Equal(UserNotificationRoutes.Plans, stay.Route);
    }

    [Fact]
    public void No_push_copy_takes_or_prints_an_amount()
    {
        foreach (var t in new[] { typeof(StayOwnerPushCopy), typeof(TaxiDriverPushCopy) })
            foreach (var m in t.GetMethods().Where(m => m.IsStatic && m.ReturnType == typeof(OwnerPush)))
                Assert.DoesNotContain(m.GetParameters(), p => p.Name!.Contains("amount", StringComparison.OrdinalIgnoreCase)
                    || p.Name!.Contains("fare", StringComparison.OrdinalIgnoreCase) || p.Name!.Contains("price", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Rs", StayOwnerPushCopy.MorningCheckIns(User, Listing, Today, [new OwnerReminderLine("Auto", "Neha", "Family Room", null)]).Body);
    }
}

public class OwnerNotifierPayloadTests
{
    [Fact]
    public void Payload_is_an_inbox_less_push_of_feature_owner_addressed_by_userId()
    {
        var user = Guid.NewGuid();
        var push = StayOwnerPushCopy.MorningCheckIns(user, Guid.NewGuid(), Be4Facts.Today, [new OwnerReminderLine("Auto", "Neha", null, null)]);
        using var doc = JsonDocument.Parse(OwnerNotifier.Payload(push));
        var root = doc.RootElement;
        Assert.Equal(user.ToString(), root.GetProperty(OwnerNotifier.UserIdProperty).GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("notificationId").ValueKind);
        Assert.Equal(NotificationTypes.OwnerMorningReminder, root.GetProperty("kind").GetString());
        Assert.Equal("owner", root.GetProperty("data").GetProperty("feature").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("expiresAtUtc").ValueKind);
    }

    [Fact]
    public void Push_sql_targets_only_active_users_with_a_valid_token_and_drops_duplicate_keys()
    {
        Assert.Contains("\"IsValid\"", OwnerNotifier.PushSql);
        Assert.Contains("\"IsActive\"", OwnerNotifier.PushSql);
        Assert.Contains("ON CONFLICT (\"Key\") DO NOTHING", OwnerNotifier.PushSql);
    }

    [Fact]
    public void Account_deletion_deletes_unsent_pushes_by_the_pinned_property()
    {
        Assert.Contains($"->> '{OwnerNotifier.UserIdProperty}'", OwnerAccountDeletion.UnsentPushesSql);
        Assert.Contains("\"PublishedAt\" IS NULL", OwnerAccountDeletion.UnsentPushesSql);
        Assert.Equal(50, new OwnerAccountDeletion(null!).Order);
    }
}

public class OwnerJobRulesTests
{
    [Fact]
    public void Festival_windows_cover_three_days_ending_at_the_lead()
    {
        var w = FestivalReminderJob.Windows(Be4Facts.Today);
        Assert.Equal(Be4Facts.Today.AddDays(19), w.StayFrom);
        Assert.Equal(Be4Facts.Today.AddDays(21), w.StayTo);
        Assert.Equal(Be4Facts.Today.AddDays(5), w.TaxiFrom);
        Assert.Equal(Be4Facts.Today.AddDays(7), w.TaxiTo);
        Assert.Equal([ListingKinds.Stay], FestivalReminderJob.KindsFor(Be4Facts.Today.AddDays(20), w));
        Assert.Equal([ListingKinds.Taxi], FestivalReminderJob.KindsFor(Be4Facts.Today.AddDays(7), w));
        Assert.Empty(FestivalReminderJob.KindsFor(Be4Facts.Today.AddDays(22), w));
        Assert.Empty(FestivalReminderJob.KindsFor(Be4Facts.Today.AddDays(18), w));
    }

    [Fact]
    public void Festival_plan_stores_ids_only_and_one_push_per_owner()
    {
        var f = new FestivalReminderJob.FestivalRow { Id = Guid.NewGuid(), Name = "Diwali", DateFrom = Be4Facts.Today.AddDays(21), DateTo = Be4Facts.Today.AddDays(25) };
        var users = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var (notices, pushes) = FestivalReminderJob.Plan(f, ListingKinds.Stay, Be4Facts.Today, users);
        Assert.All(notices, n => Assert.Equal((NoticeKinds.FestivalReminder, f.Id, f.Id), (n.Kind, n.TripId, n.RefId!.Value)));
        Assert.All(notices, n => Assert.Null(n.Amount));
        Assert.Equal(2, pushes.Count);
        Assert.All(pushes, p => Assert.Equal("Diwali is in 3 weeks", p.Title));
    }

    [Fact]
    public void Morning_lines_parse_defensively()
    {
        Assert.Empty(OwnerMorningReminderJob.ParseLines(null));
        Assert.Empty(OwnerMorningReminderJob.ParseLines("not json"));
        Assert.Empty(OwnerMorningReminderJob.ParseLines("{}"));
        var lines = OwnerMorningReminderJob.ParseLines("""[{"kind":"Manual","label":"Airport","detail":null,"time":"09:00"},{"label":"X"},3]""");
        Assert.Equal(2, lines.Count);
        Assert.Equal(new TimeOnly(9, 0), lines[0].TimeOfDay);
        Assert.Equal(CalendarEntryKinds.Manual, lines[1].Kind);
    }

    [Fact]
    public void Announce_only_while_payment_is_off_and_the_date_is_ahead()
    {
        Assert.True(OwnerPlansAnnounceJob.ShouldAnnounce(false, Be4Facts.Today.AddDays(7), Be4Facts.Today));
        Assert.False(OwnerPlansAnnounceJob.ShouldAnnounce(true, Be4Facts.Today.AddDays(7), Be4Facts.Today));
        Assert.False(OwnerPlansAnnounceJob.ShouldAnnounce(false, null, Be4Facts.Today));
        Assert.False(OwnerPlansAnnounceJob.ShouldAnnounce(false, Be4Facts.Today, Be4Facts.Today));
        Assert.Equal(20270201, OwnerPlansAnnounceJob.DateNumber(new DateOnly(2027, 2, 1)));
        Assert.True(PaymentAnnounceRules.IsFarEnough(Be4Facts.Today.AddDays(7), Be4Facts.Today));
        Assert.False(PaymentAnnounceRules.IsFarEnough(Be4Facts.Today.AddDays(6), Be4Facts.Today));
    }

    [Theory]
    [InlineData(ListingKinds.Stay)]
    [InlineData(ListingKinds.Taxi)]
    public void Announce_batch_skips_owners_already_announced_for_the_date(string kind)
    {
        var sql = OwnerPlansAnnounceJob.BatchSql(kind);
        Assert.Contains("SELECT DISTINCT l.\"UserId\"", sql);
        Assert.Contains($"an.\"Kind\" = '{NoticeKinds.PlansStarting}' AND an.\"TripId\" = @noTrip", sql);
        Assert.Contains("an.\"Amount\" = @dateNumber", sql);
    }

    [Fact]
    public void Announce_pushes_use_free_values_and_the_trial_days()
    {
        var users = new[] { Guid.NewGuid() };
        var stay = Assert.Single(OwnerPlansAnnounceJob.Pushes(ListingKinds.Stay, new DateOnly(2027, 2, 1), OwnerPlanCatalog.Seeded(trialDays: 7), users));
        Assert.Equal("Free keeps 2 photos and 2 room types. Try Plus free for 7 days.", stay.Body);
        var taxi = Assert.Single(OwnerPlansAnnounceJob.Pushes(ListingKinds.Taxi, new DateOnly(2027, 2, 1), OwnerPlanCatalog.Seeded(plusEnabled: false), users));
        Assert.Equal("Free keeps 1 route.", taxi.Body);
    }

    [Fact]
    public void Announce_pushes_of_the_seeded_catalog_never_mention_a_trial()
    {
        var users = new[] { Guid.NewGuid() };
        var stay = Assert.Single(OwnerPlansAnnounceJob.Pushes(ListingKinds.Stay, new DateOnly(2027, 2, 1), OwnerPlanCatalog.Seeded(), users));
        Assert.Equal("Free keeps 2 photos and 2 room types.", stay.Body);
        var taxi = Assert.Single(OwnerPlansAnnounceJob.Pushes(ListingKinds.Taxi, new DateOnly(2027, 2, 1), OwnerPlanCatalog.Seeded(), users));
        Assert.Equal("Free keeps 1 route.", taxi.Body);
        Assert.DoesNotContain("free for", stay.Body + taxi.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Morning_job_selection_needs_reminders_plan_value_switch_token_and_approval()
    {
        var job = new TaxiMorningReminderJob(null!, null!, null!, null!, Microsoft.Extensions.Options.Options.Create(new OwnerFeatureOptions()),
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxiMorningReminderJob>.Instance);
        var sql = job.BatchSql("pv (\"PlanId\") AS (VALUES ('00000000-0000-0000-0000-000000000000'::uuid))");
        Assert.Contains($"pv.\"{PlanFeatureKeys.Reminders}\" = 1", sql);
        Assert.Contains("l.\"RemindersOn\"", sql);
        Assert.Contains("\"IsValid\"", sql);
        Assert.Contains("l.\"Status\" = 'Approved'", sql);
        Assert.Contains("NOT e.\"Cancelled\"", sql);
        Assert.Contains("u.\"IsActive\"", sql);
        Assert.Equal(new TimeOnly(7, 0), job.DailyAtIst);
        Assert.Equal("owner.reminder.taxi", job.Name);
    }

    [Fact]
    public void Retention_and_role_cleanup_name_every_owner_notice_kind()
    {
        Assert.Equal(5, NoticeKinds.Owner.Count);
        Assert.Contains("\"UserNotices\"", RentNearBy.Infrastructure.Services.ListingPurgeService.OwnerRoleNoticesSql);
        Assert.Contains("\"FestivalReminderSent\"", RentNearBy.Infrastructure.Services.ListingPurgeService.FestivalMarksSql);
    }
}

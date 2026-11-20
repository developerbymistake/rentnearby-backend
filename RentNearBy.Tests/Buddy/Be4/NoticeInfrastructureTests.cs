using System.Text.Json;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be4;
using RentNearBy.Infrastructure.Services.Retention;
using RentNearBy.Infrastructure.Services.Retention.Buddy;

namespace RentNearBy.Tests.Buddy.Be4;

// The notification policy (FINAL_DECISIONS, contract 4): upsert in place, no text stored, coalesced pushes through the outbox.
public class NoticeInfrastructureTests
{
    private static readonly Guid Trip = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid User = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Theory]
    [InlineData(NoticeKinds.BuddyRequest, "/buddies", "tab", "requests")]
    [InlineData(NoticeKinds.PaymentSaidPaid, "/trip-expenses", "tab", "balances")]
    [InlineData(NoticeKinds.SettleReminder, "/trip-expenses", "tripId", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData(NoticeKinds.PaymentNotReceived, "/trip-expenses", "tripId", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData(NoticeKinds.ExpensesChanged, "/trip-expenses", "tripId", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData(NoticeKinds.TripRemoved, "/trip-view", "tripId", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData(NoticeKinds.TripDeleted, "/my-trips", "deletedTripId", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    public void Every_stored_kind_has_its_route(string kind, string route, string argKey, string argValue)
    {
        var r = NoticeRoutes.For(kind, Trip, null);
        Assert.Equal(route, r.Route);
        Assert.Equal(argValue, r.Args[argKey]);
    }

    [Fact]
    public void Trip_invite_routes_to_the_member_id()
    {
        var member = Guid.NewGuid();
        var r = NoticeRoutes.For(NoticeKinds.TripInvite, Trip, member);
        Assert.Equal("/trip-invite", r.Route);
        Assert.Equal(member.ToString(), r.Args["memberId"]);
        Assert.Single(r.Args);
    }

    [Fact]
    public void Every_kind_of_the_check_constraint_is_routed()
        => Assert.All(NoticeKinds.All, k => Assert.NotEqual(UserNotificationRoutes.Notifications, NoticeRoutes.For(k, Trip, Guid.NewGuid()).Route));

    [Fact]
    public void Upsert_is_one_row_per_user_kind_trip_in_place()
    {
        Assert.Contains("ON CONFLICT (\"UserId\", \"Kind\", \"TripId\") DO UPDATE", NoticeWriter.UpsertSql);
        Assert.Contains("\"Count\" = CASE WHEN n.\"ReadAt\" IS NULL THEN n.\"Count\" + 1 ELSE 1 END", NoticeWriter.UpsertSql);
        Assert.Contains("\"ReadAt\" = NULL", NoticeWriter.UpsertSql);
        Assert.Contains("unnest(", NoticeWriter.UpsertSql);
        Assert.DoesNotContain("Title", NoticeWriter.UpsertSql);
        Assert.DoesNotContain("Body", NoticeWriter.UpsertSql);
        Assert.Contains("WHERE n.\"UpdatedAt\" < @notBefore", NoticeWriter.UpsertIfOlderSql);
    }

    [Fact]
    public void Duplicates_in_one_call_keep_the_last_row()
    {
        var actor = Guid.NewGuid();
        var rows = NoticeWriter.Distinct(
        [
            new NoticeUpsert(User, NoticeKinds.ExpensesChanged, Trip, Guid.NewGuid()),
            new NoticeUpsert(User, NoticeKinds.ExpensesChanged, Trip, actor),
            new NoticeUpsert(User, NoticeKinds.SettleReminder, Trip, actor, null, 10),
        ]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(actor, rows.Single(r => r.Kind == NoticeKinds.ExpensesChanged).ActorUserId);
    }

    [Fact]
    public void Pushes_are_set_based_outbox_rows_deduped_by_key_and_delayed()
    {
        Assert.Contains("INSERT INTO \"OutboxMessages\"", NoticeWriter.PushSql);
        Assert.Contains("ON CONFLICT (\"Key\") DO NOTHING", NoticeWriter.PushSql);
        Assert.Contains("make_interval(secs => u.delay)", NoticeWriter.PushSql);
        Assert.Contains("unnest(", NoticeWriter.PushSql);
    }

    [Fact]
    public void Push_payload_has_no_inbox_row_and_carries_kind_route_and_collapse()
    {
        var spec = BuddyPushCopy.TripExpensesUpdated(User, Trip, "Rahul", "Uttarakhand Hills Trip", new DateTime(2026, 5, 19, 4, 0, 0, DateTimeKind.Utc));
        var payload = JsonSerializer.Deserialize<OutboxPushPayload>(NoticeWriter.Payload(spec), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(payload.NotificationId);
        Assert.Equal(NotificationTypes.TripExpensesUpdated, payload.Kind);
        Assert.Equal(spec.Key, payload.Key);
        Assert.Equal(spec.Tag, payload.Tag);
        Assert.Equal(UserNotificationRoutes.TripExpenses, payload.Route);
        Assert.Equal("1", payload.Data!["collapse"]);
        Assert.Equal("buddy", payload.Data["feature"]);
        Assert.Equal(User, payload.UserId);
    }

    [Fact]
    public void Must_arrive_pushes_carry_no_collapse_flag()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        PushSpec[] mustArrive =
        [
            BuddyPushCopy.ManualReminder(User, Trip, "T", [("Rahul", 100L)], new DateOnly(2026, 5, 19)),
            BuddyPushCopy.AutoReminder(User, Trip, "T", [("Rahul", 100L)], 3),
            BuddyPushCopy.PaymentSaidPaid(User, Guid.NewGuid(), Trip, "Aman", 100, "T", "Rahul", toOwner: true),
            BuddyPushCopy.PaymentNotReceived(User, Guid.NewGuid(), Trip, "Rahul", 100),
        ];
        Assert.All(mustArrive, spec =>
        {
            var payload = JsonSerializer.Deserialize<OutboxPushPayload>(NoticeWriter.Payload(spec), json)!;
            Assert.False(payload.Data!.ContainsKey("collapse"));
            Assert.Equal("buddy", payload.Data["feature"]);
        });
        Assert.All(NoticeWriter.CollapsingKinds, k => Assert.Contains(k, NotificationTypes.BuddyPushKinds));
        Assert.DoesNotContain(NotificationTypes.TripSettleReminder, NoticeWriter.CollapsingKinds);
        Assert.DoesNotContain(NotificationTypes.TripPaymentSaidPaid, NoticeWriter.CollapsingKinds);
        Assert.DoesNotContain(NotificationTypes.TripInvite, NoticeWriter.CollapsingKinds);
        Assert.DoesNotContain(NotificationTypes.BuddyRequestReceived, NoticeWriter.CollapsingKinds);
    }

    [Fact]
    public void Expense_pushes_coalesce_per_10_minute_bucket()
    {
        var t0 = new DateTime(2026, 5, 19, 4, 0, 0, DateTimeKind.Utc);
        var a = BuddyPushCopy.TripExpensesUpdated(User, Trip, "Rahul", "T", t0);
        var b = BuddyPushCopy.TripExpensesUpdated(User, Trip, "Rahul", "T", t0.AddMinutes(9));
        var c = BuddyPushCopy.TripExpensesUpdated(User, Trip, "Rahul", "T", t0.AddMinutes(10));
        Assert.Equal(a.Key, b.Key);
        Assert.NotEqual(a.Key, c.Key);
        Assert.Equal(TimeSpan.FromSeconds(60), a.Delay);
    }

    [Fact]
    public void Reader_maps_a_row_without_text_and_hides_the_zero_trip()
    {
        var now = DateTime.UtcNow;
        var dto = NoticeReader.ToDto(new NoticeReader.Row
        {
            Id = Guid.NewGuid(), Kind = NoticeKinds.BuddyRequest, TripId = NoticeKinds.NoTrip, ActorName = "Neha", Count = 2,
            CreatedAt = now, UpdatedAt = now, ReadAt = null,
        });
        Assert.Null(dto.TripId);
        Assert.False(dto.IsRead);
        Assert.Equal("/buddies", dto.Route);
        Assert.Equal(2, dto.Count);
        Assert.Equal(DateTimeKind.Utc, dto.UpdatedAt.Kind);
    }

    [Fact]
    public void Notice_page_is_keyset_on_updated_at_and_id_with_live_title_and_actor()
    {
        Assert.Contains("(n.\"UpdatedAt\", n.\"Id\") < (@at, @id)", NoticeReader.NextPageSql);
        Assert.Contains("ORDER BY n.\"UpdatedAt\" DESC, n.\"Id\" DESC", NoticeReader.FirstPageSql);
        Assert.Contains("LEFT JOIN \"Trips\"", NoticeReader.FirstPageSql);
        Assert.Contains("LEFT JOIN \"Users\"", NoticeReader.FirstPageSql);
        Assert.DoesNotContain("UpdatedAt\" =", NoticeReader.MarkReadSql);
    }

    [Fact]
    public void Retention_rules_are_30_day_notices_and_90_day_ended_payments()
    {
        var rules = new Be4RetentionRules().Create().Cast<DeclarativeRetentionRule>().ToDictionary(r => r.Name);
        Assert.Equal(30, rules["UserNotices"].DefaultDays);
        Assert.True(rules["UserNotices"].GuardExempt);
        Assert.Equal(RetentionGroups.Notifications, rules["UserNotices"].Group);
        Assert.Contains("SKIP LOCKED", rules["UserNotices"].BuildDeleteSql());
        Assert.Contains("\"UserNotices\"", rules["UserNotices"].BuildDeleteSql());
        Assert.Equal(90, rules["TripPaymentsEnded"].DefaultDays);
        Assert.Equal(RetentionGroups.TripHousekeeping, rules["TripPaymentsEnded"].Group);
        Assert.Contains("\"EndedAt\" IS NOT NULL", rules["TripPaymentsEnded"].Definition.EligibleSql);
        Assert.True(RetentionSchedule.OrderOf("UserNotices") < RetentionSchedule.RuleOrder.Count);
        Assert.True(RetentionSchedule.OrderOf("TripPaymentsEnded") < RetentionSchedule.RuleOrder.Count);
    }
}

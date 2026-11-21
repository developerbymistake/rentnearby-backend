using System.Text.Json;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be4;

namespace RentNearBy.Tests.Buddy.Be4;

// Owner order 2026-11-21: accepted, new buddy, joined, plan updated, new expense and share changed are stored inbox rows
// (frames 2.12, 3.5, 4.11), ids and numbers only, one row per user + kind + trip.
public class InboxRowKindsTests
{
    private static readonly Guid Trip = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid Owner = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Aman = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Neha = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Expense = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid Expense2 = Guid.Parse("55555555-5555-4555-8555-555555555555");

    private static readonly string[] NewKinds =
        [NoticeKinds.BuddyAccepted, NoticeKinds.NewBuddy, NoticeKinds.TripJoined, NoticeKinds.PlanUpdated, NoticeKinds.ExpenseAdded, NoticeKinds.ShareChanged];

    [Fact]
    public void New_kinds_have_the_wire_names_and_are_in_the_check_constraint()
    {
        Assert.Equal(["BuddyAccepted", "NewBuddy", "TripJoined", "PlanUpdated", "ExpenseAdded", "ShareChanged"], NewKinds);
        Assert.All(NewKinds, k =>
        {
            Assert.Contains(k, NoticeKinds.All);
            Assert.Contains($"'{k}'", NoticeKinds.SqlList);
            Assert.True(k.Length <= 20);
        });
        Assert.Equal(14, NoticeKinds.All.Count);
        Assert.Equal(NoticeKinds.All.Count, NoticeKinds.All.Distinct().Count());
        Assert.Contains(NoticeKinds.ExpenseAdded, NoticeKinds.WithAmount);
        Assert.Contains(NoticeKinds.ShareChanged, NoticeKinds.WithAmount);
        Assert.Equal([NoticeKinds.PlanUpdated, NoticeKinds.ExpenseAdded, NoticeKinds.ShareChanged], NoticeKinds.GoneOnRemove);
    }

    [Theory]
    [InlineData(NoticeKinds.BuddyAccepted, "/buddies", "tab", "buddies")]
    [InlineData(NoticeKinds.NewBuddy, "/buddies", "tab", "buddies")]
    [InlineData(NoticeKinds.TripJoined, "/trip-travellers", "tripId", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData(NoticeKinds.PlanUpdated, "/trip-view", "tripId", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData(NoticeKinds.ExpenseAdded, "/trip-expenses", "tripId", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData(NoticeKinds.ShareChanged, "/trip-expenses", "tripId", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    public void New_kinds_route_to_the_right_screen(string kind, string route, string argKey, string argValue)
    {
        var r = NoticeRoutes.For(kind, Trip, Expense);
        Assert.Equal(route, r.Route);
        Assert.Equal(argValue, r.Args[argKey]);
        Assert.Single(r.Args);
    }

    [Fact]
    public void Writer_moves_actor_ref_and_amount_on_the_in_place_upsert()
    {
        Assert.Contains("\"ActorUserId\" = EXCLUDED.\"ActorUserId\"", NoticeWriter.UpsertSql);
        Assert.Contains("\"RefId\" = EXCLUDED.\"RefId\"", NoticeWriter.UpsertSql);
        Assert.Contains("\"Amount\" = EXCLUDED.\"Amount\"", NoticeWriter.UpsertSql);
        Assert.Contains("\"UpdatedAt\" = now()", NoticeWriter.UpsertSql);
        Assert.Contains("unnest(@kinds, @users)", NoticeWriter.DeletePairsSql);
        var rows = NoticeWriter.Distinct(
        [
            new NoticeUpsert(Owner, NoticeKinds.TripJoined, Trip, Aman),
            new NoticeUpsert(Owner, NoticeKinds.TripJoined, Trip, Neha),
            new NoticeUpsert(Aman, NoticeKinds.BuddyAccepted, NoticeKinds.NoTrip, Neha),
            new NoticeUpsert(Neha, NoticeKinds.NewBuddy, NoticeKinds.NoTrip, Aman),
        ]);
        Assert.Equal(3, rows.Count);
        Assert.Equal(Neha, rows.Single(r => r.Kind == NoticeKinds.TripJoined).ActorUserId);
    }

    [Fact]
    public void Reader_dto_carries_ids_and_numbers_under_the_wire_names()
    {
        var now = DateTime.UtcNow;
        var dto = NoticeReader.ToDto(new NoticeReader.Row
        {
            Id = Guid.NewGuid(), Kind = NoticeKinds.ExpenseAdded, TripId = Trip, TripTitle = "Goa", ActorUserId = Owner, ActorName = "Rahul",
            RefId = Expense, Amount = 6400, Count = 3, CreatedAt = now, UpdatedAt = now,
        });
        Assert.Equal("/trip-expenses", dto.Route);
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("ExpenseAdded", root.GetProperty("kind").GetString());
        Assert.Equal(Owner, root.GetProperty("actorUserId").GetGuid());
        Assert.Equal(Expense, root.GetProperty("refId").GetGuid());
        Assert.Equal(6400, root.GetProperty("amount").GetInt32());
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        Assert.Equal("Rahul", root.GetProperty("actorName").GetString());

        var buddy = NoticeReader.ToDto(new NoticeReader.Row { Id = Guid.NewGuid(), Kind = NoticeKinds.BuddyAccepted, TripId = NoticeKinds.NoTrip, ActorUserId = Aman, Count = 2, CreatedAt = now, UpdatedAt = now });
        Assert.Null(buddy.TripId);
        Assert.Equal("/buddies", buddy.Route);
        Assert.Equal("buddies", buddy.Args["tab"]);
    }

    [Fact]
    public void Pushes_of_these_events_stay_collapsing_and_carry_no_inbox_id()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        PushSpec[] specs =
        [
            BuddyPushCopy.BuddyRequestAccepted(Aman, Guid.NewGuid(), "Neha"),
            BuddyPushCopy.TripMemberJoined(Owner, Guid.NewGuid(), 1, Trip, "Aman", "Goa", 2),
            BuddyPushCopy.TripPlanUpdated(Aman, Trip, "Neha", "Goa", DateTime.UtcNow),
            BuddyPushCopy.TripExpensesUpdated(Aman, Trip, "Rahul", "Goa", DateTime.UtcNow),
        ];
        Assert.All(specs, spec =>
        {
            var payload = JsonSerializer.Deserialize<OutboxPushPayload>(NoticeWriter.Payload(spec), json)!;
            Assert.Null(payload.NotificationId);
            Assert.Equal("1", payload.Data!["collapse"]);
            Assert.Equal(spec.PushKind, payload.Kind);
        });
        Assert.Equal(TimeSpan.FromMinutes(10), specs[2].Delay);
    }

    private static ExpenseFacts Facts(params (Guid User, int Share)[] shares)
        => new(shares.Sum(s => s.Share), "Taxi", ExpenseCategories.Taxi, 1, null, null, Owner, SplitModes.AllTravellers, null, null,
            shares.Select(s => new SplitShare(s.User, s.Share)).ToList());

    [Fact]
    public void A_create_gives_ExpenseAdded_with_each_share_and_nothing_else()
    {
        var now = Facts((Owner, 6400), (Aman, 6400), (Neha, 6400));
        var rows = TripExpenseRules.MoneyNotices(Trip, Owner, [Aman, Neha], [(Expense, null, now)]);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(NoticeKinds.ExpenseAdded, r.Kind);
            Assert.Equal(Expense, r.RefId);
            Assert.Equal(6400, r.Amount);
            Assert.Equal(Owner, r.ActorUserId);
            Assert.Equal(Trip, r.TripId);
        });
    }

    [Fact]
    public void An_edit_gives_ShareChanged_to_moved_shares_and_ExpensesChanged_to_the_rest()
    {
        var old = Facts((Owner, 6400), (Aman, 6400), (Neha, 6400));
        var now = Facts((Owner, 6400), (Aman, 6166), (Neha, 6400));
        var rows = TripExpenseRules.MoneyNotices(Trip, Owner, [Aman, Neha], [(Expense, old, now)]);
        var aman = rows.Single(r => r.UserId == Aman);
        Assert.Equal(NoticeKinds.ShareChanged, aman.Kind);
        Assert.Equal(6166, aman.Amount);
        Assert.Equal(Expense, aman.RefId);
        var neha = rows.Single(r => r.UserId == Neha);
        Assert.Equal(NoticeKinds.ExpensesChanged, neha.Kind);
        Assert.Null(neha.Amount);
        Assert.Single(rows, r => r.UserId == Neha);
    }

    [Fact]
    public void A_person_dropped_from_the_split_gets_ShareChanged_to_zero()
    {
        var old = Facts((Owner, 100), (Aman, 100));
        var now = Facts((Owner, 200));
        var row = Assert.Single(TripExpenseRules.MoneyNotices(Trip, Owner, [Aman], [(Expense, old, now)]));
        Assert.Equal(NoticeKinds.ShareChanged, row.Kind);
        Assert.Equal(0, row.Amount);
    }

    [Fact]
    public void A_delete_gives_ExpensesChanged_only()
    {
        var old = Facts((Owner, 100), (Aman, 100));
        var row = Assert.Single(TripExpenseRules.MoneyNotices(Trip, Owner, [Aman], [(Expense, old, null)]));
        Assert.Equal(NoticeKinds.ExpensesChanged, row.Kind);
        Assert.Null(row.RefId);
    }

    [Fact]
    public void Share_with_over_several_expenses_keeps_the_last_moved_share()
    {
        var rows = TripExpenseRules.MoneyNotices(Trip, Owner, [Aman, Neha],
        [
            (Expense, Facts((Owner, 300), (Aman, 300)), Facts((Owner, 200), (Aman, 200), (Neha, 200))),
            (Expense2, Facts((Owner, 90), (Aman, 90)), Facts((Owner, 60), (Aman, 60), (Neha, 60))),
        ]);
        var aman = rows.Single(r => r.UserId == Aman);
        Assert.Equal((NoticeKinds.ShareChanged, Expense2, 60), (aman.Kind, aman.RefId!.Value, aman.Amount!.Value));
        var neha = rows.Single(r => r.UserId == Neha);
        Assert.Equal((NoticeKinds.ShareChanged, Expense2, 60), (neha.Kind, neha.RefId!.Value, neha.Amount!.Value));
    }
}

// PlanUpdated.Amount = the one day whose places changed; null when more than one day or anything else moved.
public class PlanChangedDayTests
{
    private static TripPlan Plan(params string[][] days) => new()
    {
        Days = days.Select((spots, i) => new RentNearBy.Core.Models.TripDay { Day = i + 1, Spots = spots.Select(id => new TripSpot { Id = id }).ToList() }).ToList(),
    };

    [Fact]
    public void One_changed_day_is_named()
        => Assert.Equal(2, RentNearBy.Core.Models.TaxiRequests.TripPlanLock.OnlyChangedDay(Plan(["a"], ["b"], ["c"]), Plan(["a"], ["b", "x"], ["c"])));

    [Fact]
    public void Two_changed_days_or_a_new_day_give_null()
    {
        Assert.Null(RentNearBy.Core.Models.TaxiRequests.TripPlanLock.OnlyChangedDay(Plan(["a"], ["b"]), Plan(["x"], ["y"])));
        Assert.Null(RentNearBy.Core.Models.TaxiRequests.TripPlanLock.OnlyChangedDay(Plan(["a"]), Plan(["a"], ["b"])));
        Assert.Null(RentNearBy.Core.Models.TaxiRequests.TripPlanLock.OnlyChangedDay(Plan(["a"]), Plan(["a"])));
    }

    [Fact]
    public void A_settings_change_gives_null()
    {
        var after = Plan(["a"], ["x"]);
        after.Settings.Adults = 4;
        Assert.Null(RentNearBy.Core.Models.TaxiRequests.TripPlanLock.OnlyChangedDay(Plan(["a"], ["b"]), after));
    }
}

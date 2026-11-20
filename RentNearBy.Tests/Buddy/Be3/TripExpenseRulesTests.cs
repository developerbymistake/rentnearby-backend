using System.Text.Json;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Tests.Buddy.Be3;

public class TripExpenseRulesTests
{
    private static readonly Guid Rahul = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Aman = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Priya = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Neha = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly IReadOnlyList<Guid> Canonical = [Rahul, Aman, Priya];

    private static readonly IReadOnlyList<TripTraveller> Travellers =
    [
        new(Rahul, "Rahul", "Bakhli#48291736", true, "Owner", null),
        new(Aman, "Aman", "Bakhli#61408275", false, TripMemberStatuses.Left, DateTime.UtcNow.AddDays(-3)),
        new(Priya, "Priya", "Bakhli#50267139", false, TripMemberStatuses.Joined, DateTime.UtcNow.AddDays(-2)),
    ];

    private static SaveExpenseRequestDto Request(int amount = 18500, string mode = "All", List<Guid>? sharers = null, List<ExpenseShareDto>? shares = null)
        => new()
        {
            OpId = Guid.NewGuid(), Amount = amount, Title = "Taxi, Suresh (Dzire), full trip", Category = "Taxi", DayNumber = 3,
            PaidByUserId = Rahul, SplitMode = mode, Sharers = sharers ?? [Rahul, Aman, Priya], Shares = shares,
        };

    private static CleanExpenseInput Clean(SaveExpenseRequestDto r)
    {
        Assert.Null(TripExpenseRules.CheckInput(r, out var clean));
        return clean!;
    }

    [Theory]
    [InlineData("2026-05-16", 7, "2026-05-19", 4)]
    [InlineData("2026-05-16", 7, "2026-05-10", 1)]
    [InlineData("2026-05-16", 7, "2026-06-30", 7)]
    public void Day_today_is_clamped_to_the_plan(string start, int days, string today, int expected)
        => Assert.Equal(expected, TripExpenseRules.DayToday(DateOnly.Parse(start), days, DateOnly.Parse(today)));

    [Fact]
    public void Day_today_without_dates_is_day_1() => Assert.Equal(1, TripExpenseRules.DayToday(null, 5, new DateOnly(2026, 5, 1)));

    [Fact]
    public void Trip_closes_for_changes_30_days_after_the_end()
    {
        var end = new DateOnly(2026, 5, 22);
        Assert.Equal(new DateOnly(2026, 6, 21), TripExpenseRules.ClosesOn(end, null));
        Assert.False(TripExpenseRules.Frozen(end, null, new DateOnly(2026, 6, 21)));
        Assert.True(TripExpenseRules.Frozen(end, null, new DateOnly(2026, 6, 22)));
        Assert.True(TripExpenseRules.TripEnded(end, new DateOnly(2026, 5, 23)));
        Assert.False(TripExpenseRules.TripEnded(end, end));
    }

    [Fact]
    public void Trip_without_end_date_closes_30_days_after_completion_or_never()
    {
        Assert.False(TripExpenseRules.Frozen(null, null, new DateOnly(2030, 1, 1)));
        var completed = new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc);
        Assert.False(TripExpenseRules.Frozen(null, completed, new DateOnly(2026, 5, 31)));
        Assert.True(TripExpenseRules.Frozen(null, completed, new DateOnly(2026, 6, 1)));
    }

    [Theory]
    [InlineData(0, BuddyErrorTypes.InvalidAmount)]
    [InlineData(500001, BuddyErrorTypes.InvalidAmount)]
    public void Amount_outside_1_to_500000_is_refused(int amount, string error)
        => Assert.Equal(error, TripExpenseRules.CheckInput(Request(amount), out _));

    [Fact]
    public void Input_errors_have_their_own_types()
    {
        var r = Request();
        r.Title = "   ";
        Assert.Equal(BuddyErrorTypes.TitleRequired, TripExpenseRules.CheckInput(r, out _));
        r = Request(); r.Title = new string('a', 61);
        Assert.Equal(BuddyErrorTypes.TitleRequired, TripExpenseRules.CheckInput(r, out _));
        r = Request(); r.Category = "Shopping";
        Assert.Equal(BuddyErrorTypes.InvalidCategory, TripExpenseRules.CheckInput(r, out _));
        r = Request(); r.DayNumber = 0;
        Assert.Equal(BuddyErrorTypes.InvalidDay, TripExpenseRules.CheckInput(r, out _));
        r = Request(); r.Note = new string('n', 201);
        Assert.Equal(BuddyErrorTypes.NoteTooLong, TripExpenseRules.CheckInput(r, out _));
        r = Request(mode: "Chosen", sharers: []);
        Assert.Equal(BuddyErrorTypes.NoSharers, TripExpenseRules.CheckInput(r, out _));
        r = Request(mode: "Chosen", sharers: Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToList());
        Assert.Equal(BuddyErrorTypes.TooManySharers, TripExpenseRules.CheckInput(r, out _));
        r = Request(mode: "Custom", shares: [new() { UserId = Rahul, Share = -1 }]);
        Assert.Equal(BuddyErrorTypes.SharesDoNotAddUp, TripExpenseRules.CheckInput(r, out _));
        r = Request(); r.Link = new ExpenseLinkRequestDto { Kind = "Flight", Id = Guid.NewGuid() };
        Assert.Equal(BuddyErrorTypes.LinkNotInTrip, TripExpenseRules.CheckInput(r, out _));
    }

    [Fact]
    public void Title_and_note_are_cleaned()
    {
        var r = Request();
        r.Title = "  Dinner\nby   the Ganga ";
        r.Note = "   ";
        var clean = Clean(r);
        Assert.Equal("Dinner by the Ganga", clean.Title);
        Assert.Null(clean.Note);
    }

    [Fact]
    public void Equal_split_gives_the_leftover_to_the_payer()
    {
        var input = Clean(Request());
        var split = TripExpenseRules.Split(input, Canonical, new HashSet<Guid> { Rahul, Aman, Priya });
        Assert.Null(split.Error);
        Assert.Equal(new[] { new SplitShare(Rahul, 6168), new SplitShare(Aman, 6166), new SplitShare(Priya, 6166) }, split.Shares);
        Assert.Equal(Rahul, split.LeftoverUserId);
    }

    [Fact]
    public void All_travellers_with_no_list_uses_everyone_current()
    {
        var input = Clean(Request(sharers: []));
        var split = TripExpenseRules.Split(input, Canonical, new HashSet<Guid> { Rahul, Priya });
        Assert.Equal(new[] { Rahul, Priya }, split.Shares.Select(s => s.UserId));
        Assert.Equal(18500, split.Shares.Sum(s => s.Share));
    }

    [Fact]
    public void Custom_split_must_add_up()
    {
        var bad = Clean(Request(1500, "Custom", shares: [new() { UserId = Rahul, Share = 700 }, new() { UserId = Priya, Share = 700 }]));
        Assert.Equal(BuddyErrorTypes.SharesDoNotAddUp, TripExpenseRules.Split(bad, Canonical, new HashSet<Guid>()).Error);
        var good = Clean(Request(1500, "Custom", shares: [new() { UserId = Rahul, Share = 750 }, new() { UserId = Priya, Share = 750 }, new() { UserId = Aman, Share = 0 }]));
        var split = TripExpenseRules.Split(good, Canonical, new HashSet<Guid>());
        Assert.Null(split.Error);
        Assert.Null(split.LeftoverUserId);
        Assert.Equal(2, split.Shares.Count);
    }

    [Fact]
    public void Former_traveller_cannot_be_added_to_a_new_expense_but_stays_on_his_old_one()
    {
        var current = TripExpenseRules.CurrentIds(Travellers);
        Assert.Equal(new HashSet<Guid> { Rahul, Priya }, current);
        var input = Clean(Request());
        Assert.Equal(BuddyErrorTypes.SharerNotTraveller, TripExpenseRules.CheckPeople(input, current, null));

        var stored = new ExpenseFacts(1500, "Dinner", "Food", 3, null, null, Rahul, "All", null, null,
            [new(Rahul, 500), new(Aman, 500), new(Priya, 500)]);
        Assert.Null(TripExpenseRules.CheckPeople(input, current, stored));

        var takenOut = new ExpenseFacts(1500, "Dinner", "Food", 3, null, null, Rahul, "Chosen", null, null, [new(Rahul, 750), new(Priya, 750)]);
        Assert.Equal(BuddyErrorTypes.SharerNotTraveller, TripExpenseRules.CheckPeople(input, current, takenOut));
    }

    [Fact]
    public void Former_payer_stays_selectable_only_on_his_own_expense()
    {
        var current = TripExpenseRules.CurrentIds(Travellers);
        var r = Request(sharers: [Rahul, Priya]);
        r.PaidByUserId = Aman;
        var input = Clean(r);
        Assert.Equal(BuddyErrorTypes.PayerNotTraveller, TripExpenseRules.CheckPeople(input, current, null));
        var stored = new ExpenseFacts(1000, "Lunch", "Food", 1, null, null, Aman, "Chosen", null, null, [new(Rahul, 500), new(Priya, 500)]);
        Assert.Null(TripExpenseRules.CheckPeople(input, current, stored));
    }

    [Fact]
    public void Diff_lists_every_changed_field_and_money_change()
    {
        var old = new ExpenseFacts(19200, "Taxi", "Taxi", 3, null, null, Rahul, "All", Rahul, null, [new(Rahul, 6400), new(Aman, 6400), new(Priya, 6400)]);
        var now = old with { Amount = 18500, Note = "paid in cash", Shares = [new(Rahul, 6168), new(Aman, 6166), new(Priya, 6166)] };
        var changes = TripExpenseRules.Diff(old, now);
        Assert.Equal(new[] { "amount", "shares", "note" }, changes.Select(c => c.Field));
        Assert.True(TripExpenseRules.MoneyChanged(old, now));
        Assert.False(TripExpenseRules.MoneyChanged(old, old with { Title = "Cab", Category = "Other", DayNumber = 2 }));
        Assert.False(TripExpenseRules.AnyChange(old, old));

        using var doc = JsonDocument.Parse(TripExpenseRules.ChangesJson(changes));
        var first = doc.RootElement[0];
        Assert.Equal("amount", first.GetProperty("field").GetString());
        Assert.Equal(19200, first.GetProperty("old").GetInt32());
        Assert.Equal(18500, first.GetProperty("new").GetInt32());
    }

    [Fact]
    public void Affected_people_are_those_whose_share_or_paid_changed_never_the_owner()
    {
        var old = new ExpenseFacts(1000, "Lunch", "Food", 1, null, null, Aman, "Chosen", null, null, [new(Rahul, 500), new(Aman, 500)]);
        var now = old with { Title = "Lunch at Mall Road" };
        Assert.Empty(TripExpenseRules.Affected(old, now, Rahul));

        var shared = old with { Shares = [new(Rahul, 333), new(Aman, 334), new(Priya, 333)] };
        Assert.Equal(new[] { Aman, Priya }.OrderBy(x => x), TripExpenseRules.Affected(old, shared, Rahul).OrderBy(x => x));

        Assert.Equal(new[] { Aman }, TripExpenseRules.Affected(null, old, Rahul));
        Assert.Equal(new[] { Aman }, TripExpenseRules.Affected(old, null, Rahul));

        var payerSwap = old with { PaidByUserId = Rahul };
        Assert.Equal(new[] { Aman }, TripExpenseRules.Affected(old, payerSwap, Rahul));
    }

    [Fact]
    public void Edits_after_payments_need_the_acknowledge()
    {
        Assert.False(TripExpenseRules.NeedsAcknowledge(true, 0, 0, false));
        Assert.True(TripExpenseRules.NeedsAcknowledge(true, 1, 0, false));
        Assert.True(TripExpenseRules.NeedsAcknowledge(true, 0, 2, false));
        Assert.False(TripExpenseRules.NeedsAcknowledge(true, 0, 2, true));
        Assert.False(TripExpenseRules.NeedsAcknowledge(false, 3, 3, false));
    }

    [Fact]
    public void Share_with_refuses_custom_already_sharing_and_over_20()
    {
        IReadOnlyList<SplitShare> two = [new(Rahul, 4000), new(Aman, 4000)];
        Assert.Null(TripExpenseRules.CheckShareWith("All", two, Priya));
        Assert.Equal(BuddyErrorTypes.NotEqualSplit, TripExpenseRules.CheckShareWith("Custom", two, Priya));
        Assert.Equal(BuddyErrorTypes.AlreadySharing, TripExpenseRules.CheckShareWith("Chosen", two, Aman));
        var twenty = Enumerable.Range(0, 20).Select(_ => new SplitShare(Guid.NewGuid(), 1)).ToList();
        Assert.Equal(BuddyErrorTypes.TooManySharers, TripExpenseRules.CheckShareWith("Chosen", twenty, Neha));
    }

    [Fact]
    public void Share_with_preview_matches_frame_4_7()
    {
        var split = TripExpenseEngine.ShareWith(8000, Canonical, [new(Rahul, 4000), new(Aman, 4000)], Rahul, Priya);
        Assert.Equal(new[] { new SplitShare(Rahul, 2668), new SplitShare(Aman, 2666), new SplitShare(Priya, 2666) }, split.Shares);
    }

    [Fact]
    public void Shares_json_round_trips_and_dtos_follow_canonical_order()
    {
        IReadOnlyList<SplitShare> shares = [new(Priya, 6166), new(Rahul, 6168), new(Aman, 6166)];
        var parsed = TripExpenseRules.ParseShares(TripExpenseRules.SharesJson(shares));
        Assert.True(TripExpenseRules.SameShares(shares, parsed));
        Assert.Equal(new[] { Rahul, Aman, Priya }, TripExpenseRules.ShareDtos(parsed, Canonical).Select(s => s.UserId));
        Assert.Empty(TripExpenseRules.ParseShares(null));
    }

    [Fact]
    public void Taxi_label_uses_the_driver_first_name_and_vehicle()
        => Assert.Equal("Taxi, Suresh (Dzire)", TripExpenseRules.TaxiLabel("Suresh Kumar", "Dzire"));

    [Fact]
    public void Booking_day_follows_the_trip_start()
    {
        var start = new DateOnly(2026, 5, 16);
        Assert.Equal(3, TripExpenseRules.DayOfDate(start, new DateOnly(2026, 5, 18), 7));
        Assert.Equal(1, TripExpenseRules.DayOfDate(start, new DateOnly(2026, 5, 1), 7));
        Assert.Equal(1, TripExpenseRules.DayOfDate(null, new DateOnly(2026, 5, 18), 7));
        Assert.Equal(new DateOnly(2026, 5, 18), TripExpenseRules.DateOf(start, 3));
    }
}

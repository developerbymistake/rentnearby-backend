using System.Text.Json;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be3;

namespace RentNearBy.Tests.Buddy.Be3;

// The pure builders behind E1, E4, E8, E17 and the money cards, fed with the ledger's own math (TripLedgerMath).
public class TripBalanceBuildTests
{
    private static readonly Guid TripId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid Rahul = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Aman = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Priya = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Gone = Guid.Parse("99999999-9999-4999-8999-999999999999");
    private static readonly DateTime T0 = new(2026, 5, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 5, 19);

    private static readonly IReadOnlyList<TripTraveller> Travellers =
    [
        new(Rahul, "Rahul", "Bakhli#48291736", true, "Owner", null),
        new(Aman, "Aman", "Bakhli#61408275", false, TripMemberStatuses.Joined, T0.AddDays(-5)),
        new(Priya, "Priya", "Bakhli#50267139", false, TripMemberStatuses.Joined, T0.AddDays(-4)),
    ];

    private static readonly IReadOnlyList<Guid> Order = [Rahul, Aman, Priya];

    // Mock numbers: Rahul paid 29,800 share 11,618; Aman paid 1,000 share 12,816; Priya paid 1,500 share 7,866 (total 32,300).
    private static readonly TripLedgerPerson[] MockMoney =
    [
        new(Rahul, 29800, 11618, 0, 0),
        new(Aman, 1000, 12816, 0, 0),
        new(Priya, 1500, 7866, 0, 0),
    ];

    private static TripAccessInfo Access(TripAccessLevel level, DateOnly? end = null) => new(
        TripId, Rahul, "Rahul", "Uttarakhand Hills Trip", "Planned", new DateOnly(2026, 5, 16), end ?? new DateOnly(2026, 5, 22), 7, 7, null, T0,
        level, null, null, level == TripAccessLevel.Former ? "Left" : null);

    private static TripLedgerSnapshot Snapshot(TripLedgerPerson[] money, IReadOnlyList<TripLedgerPaymentRow>? payments = null, DateTime? reopened = null)
        => TripLedgerMath.Build(TripId, Order, money, payments ?? [], new LedgerStateFacts(7, 6, 32300, reopened, reopened is null ? null : Guid.NewGuid())).Snapshot;

    [Fact]
    public void Owner_balances_match_frame_4_9()
    {
        var dto = TripBalanceReader.BuildBalances(Access(TripAccessLevel.Owner), Travellers, Snapshot(MockMoney), Rahul, [], null, Today);
        Assert.Equal(new[] { 18182L, -11816L, -6366L }, dto.People.Select(p => p.Net));
        Assert.Equal(2, dto.Payments.Count);
        Assert.All(dto.Payments, p => Assert.Equal(PaymentStates.Open, p.State));
        Assert.Equal((Aman, Rahul, 11816), (dto.Payments[0].FromUserId, dto.Payments[0].ToUserId, dto.Payments[0].Amount));
        Assert.Equal((Priya, Rahul, 6366), (dto.Payments[1].FromUserId, dto.Payments[1].ToUserId, dto.Payments[1].Amount));
        Assert.All(dto.Payments, p => Assert.True(p.CanRemind));
        Assert.All(dto.Payments, p => Assert.False(p.CanSayPaid));
        Assert.Equal(18182, dto.Me.StillOpen);
        Assert.Equal(0, dto.PaymentsDone);
        Assert.Equal(2, dto.PaymentsTotal);
        Assert.False(dto.Settled);
        Assert.Null(dto.Reopened);
        Assert.Equal("1736", dto.People[0].Person.Last4);
    }

    [Fact]
    public void Viewer_sees_his_own_pay_button_and_no_owner_buttons()
    {
        var dto = TripBalanceReader.BuildBalances(Access(TripAccessLevel.Viewer), Travellers, Snapshot(MockMoney), Aman, [Aman], null, Today);
        var mine = dto.Payments.Single(p => p.FromUserId == Aman);
        Assert.True(mine.CanSayPaid);
        Assert.False(mine.CanRemind);
        Assert.True(mine.ReminderSentToday);
        Assert.False(dto.Payments.Single(p => p.FromUserId == Priya).CanSayPaid);
        Assert.Equal(-11816, dto.Me.Net);
    }

    [Fact]
    public void Said_paid_row_counts_and_only_the_owner_can_confirm_it()
    {
        TripLedgerPaymentRow[] payments = [new(Guid.NewGuid(), Aman, Rahul, 11816, PaymentStates.SaidPaid, T0, null, null, null)];
        TripLedgerPerson[] money = [new(Rahul, 29800, 11618, 0, 11816), new(Aman, 1000, 12816, 11816, 0), new(Priya, 1500, 7866, 0, 0)];
        var owner = TripBalanceReader.BuildBalances(Access(TripAccessLevel.Owner), Travellers, Snapshot(money, payments), Rahul, [], null, Today);
        var said = owner.Payments.Single(p => p.State == PaymentStates.SaidPaid);
        Assert.True(said.CanMarkReceived);
        Assert.True(said.CanMarkNotReceived);
        Assert.False(said.CanWithdraw);
        Assert.Equal(2, owner.PaymentsTotal);

        var payer = TripBalanceReader.BuildBalances(Access(TripAccessLevel.Viewer), Travellers, Snapshot(money, payments), Aman, [], null, Today);
        var his = payer.Payments.Single(p => p.State == PaymentStates.SaidPaid);
        Assert.True(his.CanWithdraw);
        Assert.False(his.CanMarkReceived);
        Assert.Equal(11816, payer.Me.SaidPaidWaiting);
    }

    [Fact]
    public void Reopened_after_an_edit_marks_open_rows()
    {
        TripLedgerPaymentRow[] payments = [new(Guid.NewGuid(), Aman, Rahul, 11816, PaymentStates.Received, T0, T0, Rahul, null)];
        TripLedgerPerson[] money = [new(Rahul, 30000, 11618, 0, 11816), new(Aman, 1000, 12916, 11816, 0), new(Priya, 1500, 7966, 0, 0)];
        var dto = TripBalanceReader.BuildBalances(Access(TripAccessLevel.Owner), Travellers, Snapshot(money, payments, T0), Rahul, [], "Taxi", Today);
        Assert.NotNull(dto.Reopened);
        Assert.Equal("Taxi", dto.Reopened!.ExpenseTitle);
        Assert.All(dto.Payments.Where(p => p.State == PaymentStates.Open), p => Assert.True(p.AfterEdit));
        Assert.Equal(1, dto.PaymentsDone);
    }

    [Fact]
    public void Former_sees_only_his_row_and_his_payments()
    {
        var travellers = Travellers.Select(t => t.UserId == Aman ? t with { Status = TripMemberStatuses.Left } : t).ToList();
        var dto = TripBalanceReader.BuildBalances(Access(TripAccessLevel.Former), travellers, Snapshot(MockMoney), Aman, [], null, Today);
        Assert.Single(dto.People);
        Assert.Equal(Aman, dto.People[0].Person.UserId);
        Assert.Equal("Left", dto.People[0].Person.Status);
        Assert.All(dto.Payments, p => Assert.True(p.FromUserId == Aman || p.ToUserId == Aman));

        var reopened = TripBalanceReader.BuildBalances(Access(TripAccessLevel.Former), travellers, Snapshot(MockMoney, null, T0), Aman, [], null, Today);
        Assert.NotNull(reopened.Reopened);
        Assert.Null(reopened.Reopened!.ExpenseId);
        Assert.Null(reopened.Reopened.ExpenseTitle);
    }

    [Fact]
    public void Deleted_account_reads_deleted_user()
    {
        TripLedgerPerson[] money = [new(Rahul, 1000, 500, 0, 0), new(Gone, 0, 500, 0, 0)];
        var dto = TripBalanceReader.BuildBalances(Access(TripAccessLevel.Owner), Travellers, Snapshot(money), Rahul, [], null, Today);
        var gone = dto.People.Single(p => p.Person.UserId == Gone);
        Assert.Equal(MoneyPeople.DeletedName, gone.Person.Name);
        Assert.Equal("Deleted", gone.Person.Status);
        Assert.Equal(Gone, dto.People[^1].Person.UserId);
    }

    [Fact]
    public void Settled_trip_has_no_rows()
    {
        TripLedgerPerson[] money = [new(Rahul, 900, 450, 0, 450), new(Aman, 0, 450, 450, 0)];
        TripLedgerPaymentRow[] payments = [new(Guid.NewGuid(), Aman, Rahul, 450, PaymentStates.Received, T0, T0, Rahul, null)];
        var dto = TripBalanceReader.BuildBalances(Access(TripAccessLevel.Owner), Travellers, Snapshot(money, payments), Rahul, [], null, Today);
        Assert.True(dto.Settled);
        Assert.Equal(1, dto.PaymentsDone);
        Assert.Equal(1, dto.PaymentsTotal);
    }

    [Fact]
    public void Summary_groups_by_category_day_and_link()
    {
        var stay = Guid.NewGuid();
        var taxi = Guid.NewGuid();
        TripBalanceReader.GroupRow[] groups =
        [
            new() { Category = "Taxi", Total = 18500, G = 7 }, new() { Category = "Stay", Total = 8000, G = 7 }, new() { Category = "Food", Total = 2500, G = 7 },
            new() { Day = 3, Total = 20000, G = 11 }, new() { Day = 1, Total = 9000, G = 11 },
            new() { LinkKind = "Taxi", LinkId = taxi, Total = 18500, G = 12 }, new() { LinkKind = "Stay", LinkId = stay, Total = 8000, G = 12 },
            new() { Total = 5800, G = 12 },
        ];
        var dto = TripBalanceReader.BuildSummary(Access(TripAccessLevel.Owner), Travellers, Snapshot(MockMoney), groups, Rahul);
        Assert.Equal(new[] { "Taxi", "Stay", "Food" }, dto.ByCategory.Select(c => c.Category));
        Assert.Equal(new[] { 1, 3 }, dto.ByDay.Select(d => d.Day));
        Assert.Equal(new DateOnly(2026, 5, 18), dto.ByDay[1].Date);
        Assert.Equal(new[] { "Stay", "Taxi" }, dto.ByLink.Select(l => l.Kind));
        Assert.Equal(3, dto.TravellerCount);
        Assert.Equal(32300, dto.Total);
        Assert.Equal(new[] { 11618L, 12816L, 7866L }, dto.PerPerson.Select(p => p.Share));
    }

    [Fact]
    public void Suggestions_are_booked_stays_with_an_amount_not_skipped_not_linked()
    {
        TripExpenseSuggestions.StayRow[] stays =
        [
            new() { Id = Guid.NewGuid(), ListingName = "Snow View Hotel", Day = 1, AgreedTotal = 8000, Status = "ConfirmedByBoth" },
            new() { Id = Guid.NewGuid(), ListingName = "Linked Stay", Day = 2, AgreedTotal = 3000, Status = "ConfirmedByBoth", Linked = true },
            new() { Id = Guid.NewGuid(), ListingName = "Skipped Stay", Day = 3, AgreedTotal = 3000, Status = "ConfirmedByBoth", Skipped = true },
            new() { Id = Guid.NewGuid(), ListingName = "No Price", Day = 4, AgreedTotal = null, Status = "ConfirmedByBoth" },
            new() { Id = Guid.NewGuid(), ListingName = "Cancelled", Day = 5, AgreedTotal = 2000, Status = "CancelledByOwner" },
        ];
        TripExpenseSuggestions.TaxiRow[] taxis =
        [
            new() { Id = Guid.NewGuid(), DriverName = "Suresh Kumar", VehicleName = "Dzire", DateFrom = new DateOnly(2026, 5, 16), Status = "Booked" },
            new() { Id = Guid.NewGuid(), DriverName = "Ravi", VehicleName = "Innova", DateFrom = new DateOnly(2026, 5, 18), Status = "Cancelled" },
        ];
        var block = TripExpenseSuggestions.Build(stays, taxis, new DateOnly(2026, 5, 16), 7, frozen: false);
        var only = Assert.Single(block.Suggestions);
        Assert.Equal(("Snow View Hotel", 8000, 1), (only.Name, only.Amount, only.Day));
        Assert.DoesNotContain(block.Links, l => l.Label == "Cancelled");
        Assert.Contains(block.Links, l => l.Label == "Taxi, Suresh (Dzire)" && l.Day == 1 && !l.Cancelled);
        Assert.Contains(block.Links, l => l.Label == "Taxi, Ravi (Innova)" && l.Day == 3 && l.Cancelled);
        Assert.Empty(TripExpenseSuggestions.Build(stays, taxis, null, 7, frozen: true).Suggestions);
    }

    [Fact]
    public void Expense_list_travellers_keep_former_people_with_money_only()
    {
        var neha = Guid.NewGuid();
        List<TripTraveller> travellers = [.. Travellers, new(neha, "Neha", "Bakhli#73019462", false, TripMemberStatuses.Left, T0.AddDays(-3))];
        var none = TripExpenseReader.ListTravellers(travellers, [Rahul, Aman], Rahul, former: false);
        Assert.Equal(new[] { Rahul, Aman, Priya }, none.Select(p => p.UserId));
        var withNeha = TripExpenseReader.ListTravellers(travellers, [neha], Rahul, former: false);
        Assert.Contains(withNeha, p => p.UserId == neha && p.Status == "Left");

        var former = TripExpenseReader.ListTravellers(travellers, [Priya], neha, former: true);
        Assert.Equal(new[] { Rahul, Priya, neha }, former.Select(p => p.UserId));
    }

    [Fact]
    public void Detail_names_payer_sharers_actors_and_people_inside_changes()
    {
        var oldPayer = Guid.NewGuid();
        var dropped = Guid.NewGuid();
        var changes = TripExpenseRules.ChangesJson(
        [
            new ExpenseChange("paidBy", oldPayer, Rahul),
            new ExpenseChange("shares", new[] { new { userId = dropped, share = 500 } }, new[] { new { userId = Rahul, share = 500 } }),
        ]);
        var lines = new List<ExpenseHistoryLineDto> { new() { ActorUserId = Rahul, Changes = TripExpenseReader.ParseChanges(changes) } };
        var ids = TripExpenseReader.NamedIds(Rahul, [new(Aman, 500)], lines);
        Assert.Equal(new HashSet<Guid> { Rahul, Aman, oldPayer, dropped }, ids);
    }

    [Fact]
    public void Standing_reads_my_settle_net_and_first_receiver()
    {
        var snapshot = Snapshot(MockMoney);
        var aman = TripMoneyCards.Standing(TripId, snapshot, Aman, 4);
        Assert.Equal(-11816, aman.MyNet);
        Assert.True(aman.HasShare);
        Assert.Equal(4, aman.SharedCount);
        Assert.Equal(Rahul, aman.OwesToUserId);
        var rahul = TripMoneyCards.Standing(TripId, snapshot, Rahul, 6);
        Assert.Null(rahul.OwesToUserId);
        Assert.Equal(18182, rahul.MyNet);
    }

    [Fact]
    public void Payment_rows_serialize_with_the_contract_names()
    {
        var rows = TripBalanceReader.BuildPaymentRows(Snapshot(MockMoney), Order, Rahul, Rahul, new HashSet<Guid>());
        var json = JsonSerializer.Serialize(rows[0], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var name in new[] { "paymentId", "fromUserId", "toUserId", "amount", "state", "saidPaidAt", "receivedAt", "receivedByUserId",
                     "afterNotReceivedAt", "afterEdit", "canSayPaid", "canWithdraw", "canMarkReceived", "canMarkNotReceived", "canRemind", "reminderSentToday" })
            Assert.Contains($"\"{name}\":", json);
    }
}

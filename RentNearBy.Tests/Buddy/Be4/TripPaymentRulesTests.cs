using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Tests.Buddy.Be4;

public class TripPaymentRulesTests
{
    private static readonly Guid Trip = Guid.NewGuid();
    private static readonly Guid Rahul = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Aman = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Priya = Guid.Parse("33333333-3333-4333-8333-333333333333");

    private static TripLedgerSnapshot Snapshot(IReadOnlyList<TripLedgerOpenRow> open, IReadOnlyList<TripLedgerPaymentRow>? payments = null)
        => TripLedgerSnapshot.Empty(Trip) with
        {
            Open = open,
            Payments = payments ?? [],
            People = [new TripLedgerPerson(Rahul, 100, 0, 0, 0), new TripLedgerPerson(Aman, 0, 50, 0, 0), new TripLedgerPerson(Priya, 0, 50, 0, 0)],
        };

    private static TripLedgerPaymentRow Paid(Guid id, Guid from, Guid to, int amount, string state = PaymentStates.SaidPaid)
        => new(id, from, to, amount, state, DateTime.UtcNow, null, null, null);

    [Fact]
    public void Said_paid_with_the_open_amount_is_ok()
    {
        var s = Snapshot([new TripLedgerOpenRow(Aman, Rahul, 11816, null)]);
        Assert.Equal((SaidPaidCheck.Ok, (int?)null), TripPaymentRules.CheckSaidPaid(s, Guid.NewGuid(), Aman, Rahul, 11816));
    }

    [Fact]
    public void Said_paid_with_another_amount_answers_balances_changed_with_the_new_amount()
    {
        var s = Snapshot([new TripLedgerOpenRow(Aman, Rahul, 12016, null)]);
        Assert.Equal((SaidPaidCheck.BalancesChanged, (int?)12016), TripPaymentRules.CheckSaidPaid(s, Guid.NewGuid(), Aman, Rahul, 11816));
    }

    [Fact]
    public void Said_paid_without_an_open_row_answers_nothing_to_pay()
    {
        var s = Snapshot([new TripLedgerOpenRow(Priya, Rahul, 500, null)]);
        Assert.Equal(SaidPaidCheck.NothingToPay, TripPaymentRules.CheckSaidPaid(s, Guid.NewGuid(), Aman, Rahul, 500).Check);
        Assert.Equal(SaidPaidCheck.NothingToPay, TripPaymentRules.CheckSaidPaid(s, Guid.NewGuid(), Rahul, Rahul, 500).Check);
    }

    [Fact]
    public void Same_op_id_is_a_replay_and_a_second_op_for_the_pair_is_nothing_to_pay()
    {
        var op = Guid.NewGuid();
        var s = Snapshot([], [Paid(op, Aman, Rahul, 11816)]);
        Assert.Equal(SaidPaidCheck.Replay, TripPaymentRules.CheckSaidPaid(s, op, Aman, Rahul, 11816).Check);
        Assert.Equal(SaidPaidCheck.NothingToPay, TripPaymentRules.CheckSaidPaid(s, Guid.NewGuid(), Aman, Rahul, 11816).Check);
    }

    [Fact]
    public void Op_id_of_another_payer_is_never_a_replay()
    {
        var op = Guid.NewGuid();
        var s = Snapshot([new TripLedgerOpenRow(Aman, Rahul, 100, null)], [Paid(op, Priya, Rahul, 500)]);
        Assert.Equal(SaidPaidCheck.NothingToPay, TripPaymentRules.CheckSaidPaid(s, op, Aman, Rahul, 100).Check);
    }

    [Fact]
    public void A_waiting_said_paid_row_of_the_pair_blocks_a_second_one_even_with_an_open_rest()
    {
        var s = Snapshot([new TripLedgerOpenRow(Aman, Rahul, 200, null)], [Paid(Guid.NewGuid(), Aman, Rahul, 11816)]);
        Assert.Equal(SaidPaidCheck.NothingToPay, TripPaymentRules.CheckSaidPaid(s, Guid.NewGuid(), Aman, Rahul, 200).Check);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5_000_000, true)]
    [InlineData(5_000_001, false)]
    [InlineData(-5, false)]
    public void Payment_amount_range(int amount, bool ok) => Assert.Equal(ok, TripPaymentRules.IsValidAmount(amount));

    [Theory]
    [InlineData(PaymentStates.Received, PaymentStates.Received, NoRowAnswer.Replay, true)]
    [InlineData(PaymentStates.Withdrawn, PaymentStates.Withdrawn, NoRowAnswer.Replay, false)]
    [InlineData(PaymentStates.NotReceived, PaymentStates.NotReceived, NoRowAnswer.Replay, false)]
    [InlineData(PaymentStates.Withdrawn, PaymentStates.Received, NoRowAnswer.Conflict, false)]
    [InlineData(PaymentStates.NotReceived, PaymentStates.Received, NoRowAnswer.Conflict, false)]
    [InlineData(PaymentStates.Received, PaymentStates.Withdrawn, NoRowAnswer.Conflict, true)]
    [InlineData(PaymentStates.Received, PaymentStates.NotReceived, NoRowAnswer.Conflict, true)]
    [InlineData(PaymentStates.Withdrawn, PaymentStates.NotReceived, NoRowAnswer.Conflict, false)]
    public void Zero_rows_answer_from_the_stored_state(string stored, string target, NoRowAnswer answer, bool showsRow)
    {
        Assert.Equal(answer, TripPaymentRules.OnNoRow(stored, target));
        Assert.Equal(showsRow, TripPaymentRules.ConflictShowsRow(stored));
    }

    [Fact]
    public void Reminder_cleanup_covers_everyone_without_an_open_row()
    {
        var stranger = Guid.NewGuid();
        var s = Snapshot([new TripLedgerOpenRow(Aman, Rahul, 100, null)]);
        var ids = TripPaymentRules.WithoutOpenRow(s, [Rahul, Aman, stranger]);
        Assert.Equal(new[] { Rahul, stranger, Priya }.OrderBy(x => x), ids.OrderBy(x => x));
    }

    [Fact]
    public void Open_total_of_a_payer_sums_all_his_rows()
    {
        var s = Snapshot([new TripLedgerOpenRow(Aman, Rahul, 100, null), new TripLedgerOpenRow(Aman, Priya, 50, null), new TripLedgerOpenRow(Priya, Rahul, 7, null)]);
        Assert.Equal(150, TripPaymentRules.OpenTotalOf(s, Aman));
        Assert.Equal(2, TripPaymentRules.OpenRowsOf(s, Aman).Count);
    }

    [Fact]
    public void Ist_day_starts_at_18_30_utc()
    {
        var now = new DateTime(2026, 5, 25, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 5, 24, 18, 30, 0, DateTimeKind.Utc), TripPaymentRules.IstDayStartUtc(now));
        var late = new DateTime(2026, 5, 25, 19, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 5, 25, 18, 30, 0, DateTimeKind.Utc), TripPaymentRules.IstDayStartUtc(late));
    }

    [Fact]
    public void Automatic_reminders_on_end_plus_3_and_plus_7_only()
    {
        var end = new DateOnly(2026, 5, 22);
        Assert.Equal(3, TripPaymentRules.AutoReminderDay(end, new DateOnly(2026, 5, 25)));
        Assert.Equal(7, TripPaymentRules.AutoReminderDay(end, new DateOnly(2026, 5, 29)));
        Assert.Null(TripPaymentRules.AutoReminderDay(end, new DateOnly(2026, 5, 26)));
        Assert.Null(TripPaymentRules.AutoReminderDay(end, new DateOnly(2026, 6, 5)));
        Assert.Equal(new[] { new DateOnly(2026, 5, 22), new DateOnly(2026, 5, 18) }, TripPaymentRules.AutoReminderEndDates(new DateOnly(2026, 5, 25)));
    }

    [Fact]
    public void The_actor_is_never_told_about_his_own_action()
    {
        Assert.False(TripPaymentRules.SaidPaidNoticeToOwner(Rahul, Rahul));
        Assert.True(TripPaymentRules.SaidPaidNoticeToOwner(Rahul, Aman));
        Assert.True(TripPaymentRules.SaidPaidCopyToReceiver(Rahul, Aman, Priya));
        Assert.False(TripPaymentRules.SaidPaidCopyToReceiver(Rahul, Aman, Rahul));
        Assert.False(TripPaymentRules.ReceivedPushToPayer(Rahul, Rahul));
        Assert.True(TripPaymentRules.ReceivedPushToPayer(Rahul, Aman));
        Assert.True(TripPaymentRules.ReceivedPushToReceiver(Rahul, Aman, Priya));
        Assert.False(TripPaymentRules.ReceivedPushToReceiver(Rahul, Priya, Rahul));
        Assert.False(TripPaymentRules.WithdrawnPushToOwner(Rahul, Rahul));
        Assert.True(TripPaymentRules.WithdrawnPushToReceiver(Rahul, Aman, Priya));
        Assert.False(TripPaymentRules.NotReceivedToPayer(Rahul, Rahul));
        Assert.True(TripPaymentRules.NotReceivedToPayer(Rahul, Aman));
    }

    [Fact]
    public void Clamp_amount_keeps_int_range()
    {
        Assert.Equal(int.MaxValue, TripPaymentRules.ClampAmount(long.MaxValue));
        Assert.Equal(0, TripPaymentRules.ClampAmount(-3));
        Assert.Equal(11816, TripPaymentRules.ClampAmount(11816));
    }
}

using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be4;

namespace RentNearBy.Tests.Buddy.Be4;

public class MoneyJobsAndGuardsTests
{
    private static readonly Guid Trip = Guid.NewGuid();
    private static readonly Guid Rahul = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Aman = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Priya = Guid.Parse("33333333-3333-4333-8333-333333333333");

    private static readonly IReadOnlyList<TripTraveller> Travellers =
    [
        new(Rahul, "Rahul", "Bakhli#48291736", true, "Owner", null),
        new(Aman, "Aman", "Bakhli#61408275", false, TripMemberStatuses.Joined, DateTime.UtcNow),
        new(Priya, "Priya", "Bakhli#50267139", false, TripMemberStatuses.Left, DateTime.UtcNow),
    ];

    private static TripLedgerSnapshot Snapshot(IReadOnlyList<TripLedgerOpenRow> open, IReadOnlyList<TripLedgerPaymentRow>? payments = null, IReadOnlyList<TripLedgerPerson>? people = null)
        => TripLedgerSnapshot.Empty(Trip) with { Open = open, Payments = payments ?? [], People = people ?? [] };

    [Fact]
    public void Morning_job_plans_one_reminder_per_payer_with_the_total_on_end_plus_3()
    {
        var today = new DateOnly(2026, 5, 25);
        var trip = new SettleReminderJob.TripRow { Id = Trip, Title = "Uttarakhand Hills Trip", EndDate = new DateOnly(2026, 5, 22), OwnerUserId = Rahul };
        var snapshot = Snapshot([new TripLedgerOpenRow(Aman, Rahul, 11816, null), new TripLedgerOpenRow(Priya, Rahul, 500, null), new TripLedgerOpenRow(Priya, Aman, 20, null)]);
        var plan = SettleReminderJob.Plan([trip], new Dictionary<Guid, IReadOnlyList<TripTraveller>> { [Trip] = Travellers },
            new Dictionary<Guid, TripLedgerSnapshot> { [Trip] = snapshot }, today);

        Assert.Equal(2, plan.Count);
        var aman = plan.Single(p => p.Notice.UserId == Aman);
        Assert.Equal(NoticeKinds.SettleReminder, aman.Notice.Kind);
        Assert.Equal(11816, aman.Notice.Amount);
        Assert.Equal(Rahul, aman.Notice.ActorUserId);
        Assert.Equal("You owe Rahul ₹11,816.", aman.Push.Body);
        Assert.EndsWith(":3", aman.Push.Key);
        var priya = plan.Single(p => p.Notice.UserId == Priya);
        Assert.Equal(520, priya.Notice.Amount);
        Assert.Equal("You owe ₹520 on Uttarakhand Hills Trip.", priya.Push.Body);
    }

    [Fact]
    public void Morning_job_skips_other_days_and_deleted_payers()
    {
        var ghost = Guid.NewGuid();
        var trip = new SettleReminderJob.TripRow { Id = Trip, Title = "T", EndDate = new DateOnly(2026, 5, 22), OwnerUserId = Rahul };
        var snapshot = Snapshot([new TripLedgerOpenRow(ghost, Rahul, 100, null)]);
        var travellers = new Dictionary<Guid, IReadOnlyList<TripTraveller>> { [Trip] = Travellers };
        var ledgers = new Dictionary<Guid, TripLedgerSnapshot> { [Trip] = snapshot };
        Assert.Empty(SettleReminderJob.Plan([trip], travellers, ledgers, new DateOnly(2026, 5, 25)));
        var snapshot2 = Snapshot([new TripLedgerOpenRow(Aman, Rahul, 100, null)]);
        Assert.Empty(SettleReminderJob.Plan([trip], travellers, new Dictionary<Guid, TripLedgerSnapshot> { [Trip] = snapshot2 }, new DateOnly(2026, 5, 24)));
        var seven = SettleReminderJob.Plan([trip], travellers, new Dictionary<Guid, TripLedgerSnapshot> { [Trip] = snapshot2 }, new DateOnly(2026, 5, 29));
        Assert.EndsWith(":7", Assert.Single(seven).Push.Key);
    }

    [Fact]
    public void Morning_job_batch_query_uses_the_end_date_index_and_keyset()
    {
        Assert.Contains("t.\"EndDate\" = ANY(@dates)", SettleReminderJob.BatchSql);
        Assert.Contains("s.\"OpenPayments\" > 0", SettleReminderJob.BatchSql);
        Assert.Contains("t.\"Id\" > @after", SettleReminderJob.BatchSql);
        Assert.Equal(new TimeOnly(9, 0), new SettleReminderJob(null!, null!, null!, null!, TimeProvider.System, null!).DailyAtIst);
    }

    [Fact]
    public void Delete_guard_lists_said_paid_before_the_open_row_of_the_pair_in_canonical_order()
    {
        var snapshot = Snapshot(
            [new TripLedgerOpenRow(Priya, Rahul, 500, null), new TripLedgerOpenRow(Aman, Rahul, 200, null)],
            [new TripLedgerPaymentRow(Guid.NewGuid(), Aman, Rahul, 11816, PaymentStates.SaidPaid, DateTime.UtcNow, null, null, null),
             new TripLedgerPaymentRow(Guid.NewGuid(), Aman, Rahul, 50, PaymentStates.Received, DateTime.UtcNow, DateTime.UtcNow, Rahul, null)]);
        var lines = TripMoneyGuard.Lines(snapshot, Travellers, Rahul);

        Assert.Equal(3, lines.Count);
        Assert.Equal((Aman, PaymentStates.SaidPaid, 11816), (lines[0].From.UserId, lines[0].State, lines[0].Amount));
        Assert.Equal((Aman, PaymentStates.Open, 200), (lines[1].From.UserId, lines[1].State, lines[1].Amount));
        Assert.Equal((Priya, PaymentStates.Open, 500), (lines[2].From.UserId, lines[2].State, lines[2].Amount));
        Assert.Equal("8275", lines[0].From.Last4);
        Assert.True(lines[0].To.IsMe);
        Assert.Equal("Left", lines[2].From.Status);
    }

    [Fact]
    public void Delete_guard_names_a_deleted_account()
    {
        var ghost = Guid.NewGuid();
        var lines = TripMoneyGuard.Lines(Snapshot([new TripLedgerOpenRow(ghost, Rahul, 10, null)]), Travellers, Rahul);
        Assert.Equal(AccountDeletionRules.DeletedUserName, Assert.Single(lines).From.Name);
    }

    [Fact]
    public void Account_deletion_blocks_on_a_non_zero_net_or_a_waiting_said_paid_row()
    {
        var owes = Snapshot([], people: [new TripLedgerPerson(Aman, 0, 100, 0, 0)]);
        Assert.True(MoneyAccountDeletion.HasMoneyToSettle(owes, Aman));
        var square = Snapshot([], people: [new TripLedgerPerson(Aman, 100, 100, 0, 0)]);
        Assert.False(MoneyAccountDeletion.HasMoneyToSettle(square, Aman));
        var waiting = Snapshot([], [new TripLedgerPaymentRow(Guid.NewGuid(), Aman, Rahul, 100, PaymentStates.SaidPaid, DateTime.UtcNow, null, null, null)],
            [new TripLedgerPerson(Aman, 0, 100, 100, 0), new TripLedgerPerson(Rahul, 100, 0, 0, 100)]);
        Assert.True(MoneyAccountDeletion.HasMoneyToSettle(waiting, Aman));
        Assert.True(MoneyAccountDeletion.HasMoneyToSettle(waiting, Rahul));
        var received = waiting with { Payments = [waiting.Payments[0] with { State = PaymentStates.Received }] };
        Assert.False(MoneyAccountDeletion.HasMoneyToSettle(received, Aman));
        Assert.Equal(10, new MoneyAccountDeletion(null!, null!, TimeProvider.System).Order);
    }
}

using RentNearBy.Core.Models.Buddy;

namespace RentNearBy.Tests.Buddy.Foundation;

public class TripLedgerMathTests
{
    private static readonly Guid TripId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Rahul = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Aman = Guid.Parse("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid Priya = Guid.Parse("c0000000-0000-0000-0000-000000000003");
    private static readonly IReadOnlyList<Guid> Order = [Rahul, Aman, Priya];
    private static readonly DateTime T0 = new(2026, 5, 20, 8, 0, 0, DateTimeKind.Utc);

    private static TripLedgerPaymentRow Pay(Guid from, Guid to, int amount, string state, DateTime at, DateTime? ended = null)
        => new(Guid.NewGuid(), from, to, amount, state, at, state == PaymentStates.Received ? at : null, null, ended);

    [Fact]
    public void Empty_trip_is_settled()
    {
        var r = TripLedgerMath.Build(TripId, Order, [], [], null).Snapshot;
        Assert.True(r.Settled);
        Assert.Empty(r.Open);
        Assert.Equal(0, r.OpenPayments);
    }

    [Fact]
    public void Rahul_pays_18500_for_three_and_two_people_owe_him()
    {
        TripLedgerPerson[] money =
        [
            new(Aman, 0, 6166, 0, 0),
            new(Rahul, 18500, 6168, 0, 0),
            new(Priya, 0, 6166, 0, 0),
        ];
        var r = TripLedgerMath.Build(TripId, Order, money, [], new LedgerStateFacts(4, 1, 18500, null, null)).Snapshot;
        Assert.Equal([Rahul, Aman, Priya], r.People.Select(p => p.UserId));
        Assert.Equal(2, r.Open.Count);
        Assert.All(r.Open, o => Assert.Equal(Rahul, o.ToUserId));
        Assert.Equal(6166, r.Open.Single(o => o.FromUserId == Aman).Amount);
        Assert.False(r.Settled);
        Assert.Equal(2, r.OpenPayments);
        Assert.Equal(4, r.Version);
        Assert.Equal(18500, r.TotalSpent);
    }

    [Fact]
    public void Said_paid_row_counts_and_keeps_the_trip_unsettled()
    {
        TripLedgerPerson[] money =
        [
            new(Rahul, 2000, 1000, 0, 1000),
            new(Aman, 0, 1000, 1000, 0),
        ];
        var said = Pay(Aman, Rahul, 1000, PaymentStates.SaidPaid, T0);
        var r = TripLedgerMath.Build(TripId, Order, money, [said], null).Snapshot;
        Assert.Empty(r.Open);
        Assert.Equal(1, r.SaidPaidCount);
        Assert.False(r.Settled);
        Assert.Equal(1, r.OpenPayments);
        Assert.Single(r.Payments);
    }

    [Fact]
    public void Not_received_row_is_hidden_and_marks_the_reopened_pair()
    {
        TripLedgerPerson[] money =
        [
            new(Rahul, 2000, 1000, 0, 0),
            new(Aman, 0, 1000, 0, 0),
        ];
        var notReceived = Pay(Aman, Rahul, 1000, PaymentStates.NotReceived, T0, ended: T0.AddHours(1));
        var r = TripLedgerMath.Build(TripId, Order, money, [notReceived], null).Snapshot;
        Assert.Empty(r.Payments);
        var open = Assert.Single(r.Open);
        Assert.Equal(T0.AddHours(1), open.AfterNotReceivedAt);
    }

    [Fact]
    public void Deleted_user_money_sorts_after_the_travellers()
    {
        var ghost = Guid.Parse("00000000-0000-0000-0000-000000000009");
        TripLedgerPerson[] money = [new(ghost, 0, 500, 0, 0), new(Rahul, 500, 0, 0, 0)];
        var r = TripLedgerMath.Build(TripId, Order, money, [], null).Snapshot;
        Assert.Equal([Rahul, ghost], r.People.Select(p => p.UserId));
        Assert.Equal(ghost, Assert.Single(r.Open).FromUserId);
    }

    [Theory]
    [InlineData(true, 1, false, true)]
    [InlineData(true, 0, false, false)]
    [InlineData(true, 1, true, false)]
    [InlineData(false, 1, false, false)]
    public void Reopen_rule(bool changed, int received, bool settled, bool expected)
        => Assert.Equal(expected, TripLedgerMath.Reopens(changed ? Guid.NewGuid() : null, received, settled));
}

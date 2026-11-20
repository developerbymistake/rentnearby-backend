using RentNearBy.Core.Models.Buddy;
using RentNearBy.Tests.Support.Buddy;

namespace RentNearBy.Tests.Buddy.Foundation;

public class BuddyPureModelTests
{
    private static readonly Guid Owner = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Viewer = Guid.Parse("b0000000-0000-0000-0000-000000000002");

    [Theory]
    [InlineData("Bakhli#48291736", "1736")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("123", "123")]
    public void Last4(string? username, string expected) => Assert.Equal(expected, MoneyPeople.Last4(username));

    [Theory]
    [InlineData("rahul", "R")]
    [InlineData("  aman ", "A")]
    [InlineData(null, "?")]
    [InlineData("", "?")]
    public void Initial(string? name, string expected) => Assert.Equal(expected, MoneyPeople.Initial(name));

    [Fact]
    public void Money_people_mark_deleted_accounts_and_keep_canonical_order()
    {
        var ghost = Guid.Parse("00000000-0000-0000-0000-000000000005");
        TripTraveller[] travellers =
        [
            new(Owner, "Rahul", "Bakhli#48291736", true, TripAccessStatuses.Owner, null),
            new(Viewer, "Aman", "Bakhli#61408275", false, TripMemberStatuses.Left, DateTime.UtcNow),
        ];
        var people = MoneyPeople.Build(travellers, [ghost, Viewer, Owner], Viewer);
        Assert.Equal([Owner, Viewer, ghost], people.Select(p => p.UserId));
        Assert.Equal("Owner", people[0].Status);
        Assert.True(people[1].IsMe);
        Assert.Equal("Left", people[1].Status);
        Assert.Equal("Deleted user", people[2].Name);
        Assert.Equal("?", people[2].Initial);
        Assert.Equal("", people[2].Last4);
        Assert.Equal("Deleted", people[2].Status);
    }

    [Fact]
    public void Push_name_adds_last4_only_on_a_name_clash()
    {
        var a = new TripTraveller(Owner, "Aman", "Bakhli#61408275", true, "Owner", null);
        var b = new TripTraveller(Viewer, "aman", "Bakhli#29583047", false, "Joined", DateTime.UtcNow);
        var c = new TripTraveller(Guid.NewGuid(), "Priya", "Bakhli#50267139", false, "Joined", DateTime.UtcNow);
        Assert.Equal("Aman 8275", MoneyPeople.PushName(a, [a, b, c]));
        Assert.Equal("Priya", MoneyPeople.PushName(c, [a, b, c]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TripMemberStatuses.Joined)]
    public void Owner_always_has_owner_access(string? status)
        => Assert.Equal(TripAccessLevel.Owner, TripAccessRules.Level(Owner, Owner, status));

    [Theory]
    [InlineData(TripMemberStatuses.Joined, TripAccessLevel.Viewer)]
    [InlineData(TripMemberStatuses.Left, TripAccessLevel.Former)]
    [InlineData(TripMemberStatuses.Removed, TripAccessLevel.Former)]
    [InlineData(TripMemberStatuses.Invited, null)]
    [InlineData(TripMemberStatuses.Declined, null)]
    [InlineData(TripMemberStatuses.Cancelled, null)]
    [InlineData(TripMemberStatuses.Expired, null)]
    [InlineData(null, null)]
    public void Member_access_levels(string? status, TripAccessLevel? expected)
        => Assert.Equal(expected, TripAccessRules.Level(Owner, Viewer, status));

    [Fact]
    public void Pair_order_matches_postgres_uuid_order()
    {
        var low = Guid.Parse("0a000000-0000-0000-0000-0000000000ff");
        var high = Guid.Parse("ff000000-0000-0000-0000-000000000001");
        Assert.Equal((low, high), BuddyPairs.Order(high, low));
        Assert.Equal(BuddyPairs.PairLockKey(low, high), BuddyPairs.PairLockKey(high, low));
    }

    [Fact]
    public void Cursors_round_trip_and_reject_garbage()
    {
        var id = Guid.NewGuid();
        Assert.True(BuddyCursors.TryDecodeName(BuddyCursors.EncodeName("rahul|x", id), out var name, out var nid));
        Assert.Equal(("rahul|x", id), (name, nid));
        var at = new DateTime(2026, 5, 20, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(BuddyCursors.TryDecodeExpense(BuddyCursors.EncodeExpense(3, at, id), out var day, out var created, out var eid));
        Assert.Equal((3, at, id), (day, created, eid));
        Assert.False(BuddyCursors.TryDecodeExpense("not-a-cursor", out _, out _, out _));
        Assert.False(BuddyCursors.TryDecodeName(BuddyCursors.EncodeExpense(3, at, id), out _, out _));
    }

    [Fact]
    public void Payment_state_maps_to_the_engine_and_back()
    {
        foreach (var s in PaymentStates.All)
            Assert.Equal(s, PaymentStates.FromEngine(PaymentStates.ToEngine(s)));
        Assert.Equal(LedgerPaymentState.SaysPaid, PaymentStates.ToEngine(PaymentStates.SaidPaid));
    }

    [Fact]
    public void Every_error_type_has_a_status_and_message()
    {
        foreach (var type in BuddyErrorTypes.All)
        {
            Assert.InRange(BuddyErrorTypes.StatusOf(type), 400, 429);
            Assert.False(string.IsNullOrWhiteSpace(BuddyErrorTypes.MessageOf(type)));
        }
        Assert.Equal(422, BuddyErrorTypes.StatusOf(BuddyErrorTypes.SharesDoNotAddUp));
        Assert.Equal("Settle up first. Open payments stay with your travellers.", BuddyErrorTypes.MessageOf(BuddyErrorTypes.TripHasOpenBalances));
    }

    [Theory]
    [InlineData("SELECT 1 FROM \"Trips\" WHERE \"Id\" = @p0 FOR UPDATE", true)]
    [InlineData("SELECT pg_advisory_xact_lock(hashtextextended(k, 0)) FROM unnest(@keys)", false)]
    [InlineData("SAVEPOINT s1", false)]
    [InlineData("SET LOCAL statement_timeout = 1000", false)]
    [InlineData("  INSERT INTO \"UserNotices\" VALUES (1)", true)]
    public void Query_counter_counts_statements_but_not_locks_or_transaction_control(string sql, bool counted)
        => Assert.Equal(counted, QueryCounter.IsCounted(sql));

    [Fact]
    public void Query_assert_fails_when_the_count_grows_with_rows()
    {
        Assert.ThrowsAny<Exception>(() => QueryAssert.AssertRuns(["a", "b"], ["a", "b", "c"], 5, 3, 30));
        Assert.ThrowsAny<Exception>(() => QueryAssert.AssertRuns(["a", "b"], ["a", "b"], 1, 3, 30));
        QueryAssert.AssertRuns(["a", "b"], ["a", "b"], 2, 3, 30);
    }
}

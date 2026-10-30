namespace RentNearBy.Tests.PlayBilling;

public class PlaySubscriptionSnapshotTests
{
    private static readonly PlaySubscriptionLineItem Early =
        new PlayLineItemBuilder().WithProduct("room_basic").WithExpiry(Dates.Utc(2026, 11, 1)).Build();
    private static readonly PlaySubscriptionLineItem Late =
        new PlayLineItemBuilder().WithProduct("room_plus").WithExpiry(Dates.Utc(2026, 12, 1)).Build();
    private static readonly PlaySubscriptionLineItem NoExpiry =
        new PlayLineItemBuilder().WithProduct("room_pro").WithExpiry(null).Build();

    [Fact]
    public void Entitled_LatestExpiry_RegardlessOfOrder()
    {
        Assert.Equal("room_plus", new PlaySnapshotBuilder().WithLineItems(Early, Late, NoExpiry).Build().Entitled?.ProductId);
        Assert.Equal("room_plus", new PlaySnapshotBuilder().WithLineItems(NoExpiry, Late, Early).Build().Entitled?.ProductId);
    }

    [Fact]
    public void Entitled_AllExpiriesNull_Null()
    {
        var snapshot = new PlaySnapshotBuilder().WithLineItems(NoExpiry).Build();

        Assert.Null(snapshot.Entitled);
        Assert.Null(snapshot.ExpiryTime);
    }

    [Fact]
    public void ExpiryTime_IsEntitledItemExpiry()
        => Assert.Equal(Dates.Utc(2026, 12, 1), new PlaySnapshotBuilder().WithLineItems(Early, Late).Build().ExpiryTime);

    [Fact]
    public void DeferredTarget_FirstItemWithoutExpiry()
    {
        var second = new PlayLineItemBuilder().WithProduct("room_max").WithExpiry(null).Build();

        Assert.Equal("room_pro", new PlaySnapshotBuilder().WithLineItems(Late, NoExpiry, second).Build().DeferredTarget?.ProductId);
        Assert.Null(new PlaySnapshotBuilder().WithLineItems(Late).Build().DeferredTarget);
    }

    [Fact]
    public void DeferredProductId_ItemReplacementWins()
    {
        var withReplacement = new PlayLineItemBuilder().WithProduct("room_plus").DeferredTo("room_lite").Build();

        Assert.Equal("room_lite", new PlaySnapshotBuilder().WithLineItems(withReplacement, NoExpiry).Build().DeferredProductId);
    }

    [Fact]
    public void DeferredProductId_NoReplacement_FallsBackToDeferredTarget()
        => Assert.Equal("room_pro", new PlaySnapshotBuilder().WithLineItems(Late, NoExpiry).Build().DeferredProductId);

    [Fact]
    public void DeferredProductId_Nothing_Null()
        => Assert.Null(new PlaySnapshotBuilder().WithLineItems(Late).Build().DeferredProductId);

    [Fact]
    public void Find_ByProductId()
    {
        var snapshot = new PlaySnapshotBuilder().WithLineItems(Early, Late).Build();

        Assert.Same(Early, snapshot.Find("room_basic"));
        Assert.Null(snapshot.Find("room_none"));
    }

    [Theory]
    [InlineData(PlayStates.Active, true)]
    [InlineData(PlayStates.Canceled, true)]
    [InlineData(PlayStates.OnHold, false)]
    public void IsEntitled_FutureExpiry_FollowsPlayStates(string state, bool expected)
        => Assert.Equal(expected, new PlaySnapshotBuilder().WithState(state).Build().IsEntitled);

    [Fact]
    public void IsEntitled_CanceledWithPastExpiry_False()
    {
        var past = new PlayLineItemBuilder().WithExpiry(Dates.Past).Build();

        Assert.False(new PlaySnapshotBuilder().WithState(PlayStates.Canceled).WithLineItems(past).Build().IsEntitled);
    }
}
